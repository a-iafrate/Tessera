using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// Fourth extracted domain handler (docs/13-piano-miglioramenti.md, F3) — the first of the two
// riskiest lotti, since Reminders is the first domain with a cross-turn
// ConversationState.PendingIntent flow (reminder.llmConfirm). Same testing shape as the other
// three handler test classes. HandleLlmReminderAsync/HandleLlmReminderConfirmCallbackAsync
// resolve their own services from an AsyncServiceScope (kept as-is from the original
// MessageProcessor code, not rewritten to take parameters) — CreateScope below wires that scope
// to the same TesseraDbContext/ReminderService/UndoService/OnboardingService instances the rest
// of the test uses, not a second, disconnected set.
public class ReminderHandlersTests : IDisposable
{
    private readonly TestWebDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly ReminderService reminders;
    private readonly UndoService undo;
    private readonly OnboardingService onboarding;
    private readonly FakeChannel channel = new();
    private readonly IStringLocalizer<Messages> localizer;

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly User user;
    private readonly ChannelAddress address = new("fake", "chat-1");

    public ReminderHandlersTests()
    {
        var accessPolicy = new TestWebDatabase.AllowAllAccessPolicy();
        reminders = new ReminderService(Db, accessPolicy);
        undo = new UndoService(Db);
        onboarding = new OnboardingService(Db);
        localizer = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();

        user = new User { Id = userId, Email = "a@example.com", PreferredCulture = "en", TimeZoneId = "UTC" };
        Db.DomainUsers.Add(user);
        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    private ReminderHandlers CreateHandlers() => new(channel, localizer, (o, a, u, featureKey, baseReply, ct) =>
    {
        finalizedReplies.Add((u, featureKey, baseReply));
        return Task.CompletedTask;
    });

    private readonly List<(Guid UserId, string FeatureKey, string BaseReply)> finalizedReplies = [];

    private AsyncServiceScope CreateScope() =>
        new ServiceCollection()
            .AddSingleton(Db)
            .AddSingleton(reminders)
            .AddSingleton(undo)
            .AddSingleton(onboarding)
            .BuildServiceProvider()
            .CreateAsyncScope();

    [Fact]
    public async Task HandleRemindCommandAsync_Empty_ReturnsListEmpty_WhenNoneArePending()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRemindCommandAsync(
            reminders, undo, onboarding, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "", CancellationToken.None);

        Assert.Equal("No pending reminders", result);
    }

    [Fact]
    public async Task HandleRemindCommandAsync_CreatesAOnceReminder_AndFinalizesReply()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRemindCommandAsync(
            reminders, undo, onboarding, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "31/12/2099 09:00 call mom", CancellationToken.None);

        Assert.Null(result); // already sent via finalizeReplyAsync
        var pending = Assert.Single(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
        Assert.Equal("call mom", pending.Text);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 9, 0, 0, TimeSpan.Zero), pending.DueAt);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("reminders", finalized.FeatureKey);
    }

    [Fact]
    public async Task HandleRemindCommandAsync_CreatesARecurringReminder_AndFinalizesReply()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRemindCommandAsync(
            reminders, undo, onboarding, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "weekly water the plants", CancellationToken.None);

        Assert.Null(result);
        var pending = Assert.Single(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
        Assert.Equal("water the plants", pending.Text);
        Assert.NotNull(pending.Recurrence);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("reminders", finalized.FeatureKey);
        Assert.Contains("weekly", finalized.BaseReply);
    }

    [Fact]
    public async Task HandleRemindCommandAsync_ReturnsUsage_WhenTheTextIsUnparseable()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRemindCommandAsync(
            reminders, undo, onboarding, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "not a valid command at all!!!", CancellationToken.None);

        Assert.StartsWith("Try: /remind", result);
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleRemindCommandAsync_Empty_ListsPendingReminders_WithACompleteButtonEach()
    {
        var handlers = CreateHandlers();
        await reminders.CreateOnceAsync(spaceId, userId, "call mom", DateTimeOffset.UtcNow.AddDays(1), "UTC", CancellationToken.None);

        var result = await handlers.HandleRemindCommandAsync(
            reminders, undo, onboarding, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "", CancellationToken.None);

        Assert.Null(result); // already sent
        var sent = Assert.Single(channel.SentChoices);
        Assert.Contains("call mom", sent.Text);
        var choice = Assert.Single(sent.Choices);
        Assert.StartsWith("remind.complete:", choice.Value);
    }

    [Fact]
    public async Task HandleReminderCompleteCallbackAsync_CompletesTheReminder_AndSendsConfirmation()
    {
        var handlers = CreateHandlers();
        var reminder = await reminders.CreateOnceAsync(spaceId, userId, "call mom", DateTimeOffset.UtcNow.AddDays(1), "UTC", CancellationToken.None);

        await handlers.HandleReminderCompleteCallbackAsync(reminders, address, spaceId, userId, reminder.Id, CancellationToken.None);

        Assert.Equal("Done: call mom", Assert.Single(channel.SentTexts).Text);
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleReminderCompleteCallbackAsync_IsStaleTapSafe_WhenAlreadyGone()
    {
        var handlers = CreateHandlers();

        await handlers.HandleReminderCompleteCallbackAsync(reminders, address, spaceId, userId, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task HandleLlmReminderAsync_AsksToConfirm_AndDoesNotCreateAnythingYet()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope();
        var args = JsonDocument.Parse("""{"text":"call mom","due_at":"2099-12-31T09:00:00"}""").RootElement;

        var result = await handlers.HandleLlmReminderAsync(scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, args, CancellationToken.None);

        Assert.Null(result); // already sent as a choice
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
        var sent = Assert.Single(channel.SentChoices);
        Assert.Contains("call mom", sent.Text);
        Assert.Equal(2, sent.Choices.Count);
        var state = Assert.Single(Db.ConversationStates);
        Assert.Equal("reminder.llmConfirm", state.PendingIntent);
    }

    [Fact]
    public async Task HandleLlmReminderAsync_ReturnsNotUnderstood_WhenDueAtIsUnparseable()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope();
        var args = JsonDocument.Parse("""{"text":"call mom","due_at":"not a date"}""").RootElement;

        var result = await handlers.HandleLlmReminderAsync(scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, args, CancellationToken.None);

        Assert.Equal("I didn't get that. Try /help", result);
        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task HandleLlmReminderConfirmCallbackAsync_Yes_CreatesTheReminder_AndFinalizesReply()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope();
        var args = JsonDocument.Parse("""{"text":"call mom","due_at":"2099-12-31T09:00:00"}""").RootElement;
        await handlers.HandleLlmReminderAsync(scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, args, CancellationToken.None);

        await handlers.HandleLlmReminderConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        var pending = Assert.Single(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
        Assert.Equal("call mom", pending.Text);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("reminders", finalized.FeatureKey);
    }

    [Fact]
    public async Task HandleLlmReminderConfirmCallbackAsync_No_SendsCancelledText_AndCreatesNothing()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope();
        var args = JsonDocument.Parse("""{"text":"call mom","due_at":"2099-12-31T09:00:00"}""").RootElement;
        await handlers.HandleLlmReminderAsync(scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, args, CancellationToken.None);

        await handlers.HandleLlmReminderConfirmCallbackAsync(scope, address, user, "no", CancellationToken.None);

        Assert.Equal("Ok, never mind.", Assert.Single(channel.SentTexts).Text);
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleLlmReminderConfirmCallbackAsync_NoOps_WhenNoPendingStateExists()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope();

        await handlers.HandleLlmReminderConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        Assert.Empty(channel.SentTexts);
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
    }
}
