using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// Last extracted domain handler (docs/13-piano-miglioramenti.md, F3) — the largest and riskiest
// lotto. CalendarQueryService sits on top of an OAuth token-refresh chain (LinkedAccountService
// -> ITokenVault -> a real HTTP call to the provider's token endpoint when its in-memory cache
// entry is cold) that this test project has no seam into: the cache key is internal to
// Tessera.Data, and no InternalsVisibleTo exists to reach it. So every scenario here either
// leaves the space with zero calendars linked (the accessible-calendars lookup short-circuits
// before ever reaching LinkedAccountService — a real, common case: most spaces never link a
// calendar) or never needs CalendarQueryService's provider-facing methods at all. What this
// intentionally does NOT cover: an actual event being created/deleted/moved against a linked
// calendar, and the "multiple matching events" branch — those all require a live access token.
// A dedicated CalendarQueryService test pass, with its own token-cache seam, is future work, not
// part of this pure-extraction commit.
public class CalendarHandlersTests : IDisposable
{
    private readonly TestWebDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly CalendarQueryService calendarQuery;
    private readonly FakeChannel channel = new();
    private readonly IStringLocalizer<Messages> localizer;
    private readonly List<(Guid UserId, string FeatureKey, string BaseReply)> finalizedReplies = [];

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly User user;
    private readonly ChannelAddress address = new("fake", "chat-1");

    public CalendarHandlersTests()
    {
        localizer = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();
        var logger = new ServiceCollection().AddLogging().BuildServiceProvider().GetRequiredService<ILogger<CalendarQueryService>>();
        var memberships = new MembershipRepository(Db, testDb.Cache);
        var linkedAccounts = new LinkedAccountService(
            Db, new NeverCalledTokenVault(), [], new NeverCalledHttpClientFactory(), new ConfigurationBuilder().Build(), testDb.Cache);
        calendarQuery = new CalendarQueryService(Db, memberships, [], linkedAccounts, logger);

        user = new User { Id = userId, Email = "a@example.com", PreferredCulture = "en", TimeZoneId = "UTC" };
        Db.DomainUsers.Add(user);
        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        // CalendarQueryService checks membership level directly (IMembershipRepository), not
        // IAccessPolicy like every other domain service — so a real Membership row is required,
        // not just a permissive fake policy.
        Db.Memberships.Add(new Membership { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = userId, IsOwner = true, JoinedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    private CalendarHandlers CreateHandlers() => new(channel, localizer, (o, a, u, featureKey, baseReply, ct) =>
    {
        finalizedReplies.Add((u, featureKey, baseReply));
        return Task.CompletedTask;
    });

    private AsyncServiceScope CreateScope(bool withCalendar)
    {
        var services = new ServiceCollection().AddSingleton(Db);
        if (withCalendar)
        {
            services.AddSingleton(calendarQuery);
        }

        return services.BuildServiceProvider().CreateAsyncScope();
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task HandleCalendarEventsQueryAsync_ReturnsNotConfigured_WhenNoQueryServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);

        var result = await handlers.HandleCalendarEventsQueryAsync(
            scope, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Equal("Calendar linking isn't available yet.", result);
    }

    [Fact]
    public async Task HandleCalendarEventsQueryAsync_ReturnsEventsEmpty_WhenNoCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleCalendarEventsQueryAsync(
            scope, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Equal("Nothing on the calendar for that range.", result);
    }

    [Fact]
    public async Task HandleCalendarEventsQueryAsync_ReturnsNotUnderstood_WhenDatesAreUnparseable()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleCalendarEventsQueryAsync(
            scope, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"not a date","to":"also not a date"}"""), CancellationToken.None);

        Assert.Equal("I didn't get that. Try /help", result);
    }

    [Fact]
    public async Task HandleCalendarFreeBusyQueryAsync_ReturnsNotConfigured_WhenNoQueryServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);

        var result = await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Equal("Calendar linking isn't available yet.", result);
    }

    // A 24-hour open range would also qualify for at least one bookable slot (E5) — narrowed to
    // 30 minutes here specifically so this test stays about the plain-text "all free" reply, not
    // the slot-offering behavior, which HandleCalendarFreeBusyQueryAsync_OffersBookableSlots_WhenAGapIsLongEnough
    // below covers instead.
    [Fact]
    public async Task HandleCalendarFreeBusyQueryAsync_ReturnsAllFree_WhenNoCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-01T00:30:00"}"""), CancellationToken.None);

        Assert.Equal("Nobody's busy in that range.", result);
        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task HandleCalendarFreeBusyQueryAsync_OffersBookableSlots_WhenAGapIsLongEnough()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Null(result); // already sent as a choice
        var sent = Assert.Single(channel.SentChoices);
        Assert.Contains("Want me to book one of these?", sent.Text);
        var choice = Assert.Single(sent.Choices);
        Assert.Equal("calendarSlot.book:0", choice.Value);
        var state = Assert.Single(Db.ConversationStates);
        Assert.Equal("calendarEvent.slotPick", state.PendingIntent);
    }

    [Fact]
    public async Task HandleCalendarFreeBusyQueryAsync_ReturnsPersonNotFound_WhenANamedPersonDoesNotResolve()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00","people":["Nobody Here"]}"""), CancellationToken.None);

        Assert.Equal("I don't see anyone matching \"Nobody Here\" in this space.", result);
    }

    [Fact]
    public async Task HandleCalendarSlotBookCallbackAsync_AsksForATitle_AndMovesThePendingIntentForward()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);
        await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        await handlers.HandleCalendarSlotBookCallbackAsync(scope, address, user, 0, CancellationToken.None);

        Assert.Contains("What should I call it?", Assert.Single(channel.SentTexts).Text);
        var state = Assert.Single(Db.ConversationStates);
        Assert.Equal("calendarEvent.slotTitle", state.PendingIntent);
    }

    [Fact]
    public async Task HandleCalendarSlotBookCallbackAsync_IsStaleTapSafe_WhenNoSlotPickIsPending()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        await handlers.HandleCalendarSlotBookCallbackAsync(scope, address, user, 0, CancellationToken.None);

        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task TryHandlePendingSlotTitleAsync_ReturnsFalse_WhenNothingIsPending()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var handled = await handlers.TryHandlePendingSlotTitleAsync(scope, address, user, "Dentist", CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task TryHandlePendingSlotTitleAsync_SendsCreateFailed_WhenNoWritableCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);
        await handlers.HandleCalendarFreeBusyQueryAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);
        await handlers.HandleCalendarSlotBookCallbackAsync(scope, address, user, 0, CancellationToken.None);

        var handled = await handlers.TryHandlePendingSlotTitleAsync(scope, address, user, "Dentist", CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(
            "I couldn't create that event — check that a calendar is set as the default for creating events in this space.",
            channel.SentTexts[^1].Text);
    }

    [Fact]
    public async Task HandleLlmCreateCalendarEventAsync_ReturnsNotConfigured_WhenNoQueryServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);

        var result = await handlers.HandleLlmCreateCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"title":"Dentist","start":"2099-01-01T09:00:00","end":"2099-01-01T10:00:00"}"""), CancellationToken.None);

        Assert.Equal("Calendar linking isn't available yet.", result);
    }

    [Fact]
    public async Task HandleLlmCreateCalendarEventAsync_AsksToConfirm_AndStoresConversationState()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleLlmCreateCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"title":"Dentist","start":"2099-01-01T09:00:00","end":"2099-01-01T10:00:00"}"""), CancellationToken.None);

        Assert.Null(result); // already sent as a choice
        var sent = Assert.Single(channel.SentChoices);
        Assert.Contains("Dentist", sent.Text);
        Assert.Equal(2, sent.Choices.Count);
        var state = Assert.Single(Db.ConversationStates);
        Assert.Equal("calendarEvent.llmConfirm", state.PendingIntent);
    }

    [Fact]
    public async Task HandleLlmCreateCalendarEventAsync_ReturnsNotUnderstood_WhenDatesAreUnparseable()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleLlmCreateCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"title":"Dentist","start":"not a date","end":"also not a date"}"""), CancellationToken.None);

        Assert.Equal("I didn't get that. Try /help", result);
        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task HandleLlmCalendarEventConfirmCallbackAsync_Yes_SendsCreateFailed_WhenNoWritableCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);
        await handlers.HandleLlmCreateCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"title":"Dentist","start":"2099-01-01T09:00:00","end":"2099-01-01T10:00:00"}"""), CancellationToken.None);

        await handlers.HandleLlmCalendarEventConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        Assert.Equal(
            "I couldn't create that event — check that a calendar is set as the default for creating events in this space.",
            Assert.Single(channel.SentTexts).Text);
    }

    [Fact]
    public async Task HandleLlmCalendarEventConfirmCallbackAsync_No_SendsCancelledText()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);
        await handlers.HandleLlmCreateCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"title":"Dentist","start":"2099-01-01T09:00:00","end":"2099-01-01T10:00:00"}"""), CancellationToken.None);

        await handlers.HandleLlmCalendarEventConfirmCallbackAsync(scope, address, user, "no", CancellationToken.None);

        Assert.Equal("Ok, never mind.", Assert.Single(channel.SentTexts).Text);
    }

    [Fact]
    public async Task HandleLlmCalendarEventConfirmCallbackAsync_NoOps_WhenNoPendingStateExists()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        await handlers.HandleLlmCalendarEventConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task HandleLlmDeleteCalendarEventAsync_ReturnsNotConfigured_WhenNoQueryServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);

        var result = await handlers.HandleLlmDeleteCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"search_text":"dentist","from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Equal("Calendar linking isn't available yet.", result);
    }

    [Fact]
    public async Task HandleLlmDeleteCalendarEventAsync_ReturnsNotFound_WhenNoCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleLlmDeleteCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"search_text":"dentist","from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00"}"""), CancellationToken.None);

        Assert.Equal("I couldn't find an event matching that in your calendar for that range.", result);
    }

    [Fact]
    public async Task HandleLlmCalendarEventDeleteConfirmCallbackAsync_NoOps_WhenNoPendingStateExists()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        await handlers.HandleLlmCalendarEventDeleteConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task HandleLlmMoveCalendarEventAsync_ReturnsNotConfigured_WhenNoQueryServiceIsRegistered()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);

        var result = await handlers.HandleLlmMoveCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"search_text":"dentist","from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00","new_start":"2099-01-03T09:00:00"}"""), CancellationToken.None);

        Assert.Equal("Calendar linking isn't available yet.", result);
    }

    [Fact]
    public async Task HandleLlmMoveCalendarEventAsync_ReturnsNotFound_WhenNoCalendarIsLinked()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        var result = await handlers.HandleLlmMoveCalendarEventAsync(
            scope, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture,
            Args("""{"search_text":"dentist","from":"2099-01-01T00:00:00","to":"2099-01-02T00:00:00","new_start":"2099-01-03T09:00:00"}"""), CancellationToken.None);

        Assert.Equal("I couldn't find an event matching that in your calendar for that range.", result);
    }

    [Fact]
    public async Task HandleLlmCalendarEventMoveConfirmCallbackAsync_NoOps_WhenNoPendingStateExists()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: true);

        await handlers.HandleLlmCalendarEventMoveConfirmCallbackAsync(scope, address, user, "yes", CancellationToken.None);

        Assert.Empty(channel.SentTexts);
    }

    [Fact]
    public async Task HandleCalendarSuggestionCallbackAsync_No_SendsDismissedText()
    {
        var handlers = CreateHandlers();
        await using var scope = CreateScope(withCalendar: false);
        var services = new ServiceCollection().AddSingleton(new ShoppingListService(Db, new TestWebDatabase.AllowAllAccessPolicy()));
        await using var shoppingScope = services.BuildServiceProvider().CreateAsyncScope();

        await handlers.HandleCalendarSuggestionCallbackAsync(shoppingScope, address, user, "calendarSuggest.no", CancellationToken.None);

        Assert.Equal("Ok, maybe next time!", Assert.Single(channel.SentTexts).Text);
    }

    [Fact]
    public async Task HandleCalendarSuggestionCallbackAsync_Yes_ShowsTheShoppingList()
    {
        var handlers = CreateHandlers();
        var shopping = new ShoppingListService(Db, new TestWebDatabase.AllowAllAccessPolicy());
        await shopping.AddItemAsync(spaceId, userId, "milk", listName: null, CancellationToken.None);
        var services = new ServiceCollection().AddSingleton(shopping);
        await using var shoppingScope = services.BuildServiceProvider().CreateAsyncScope();

        await handlers.HandleCalendarSuggestionCallbackAsync(shoppingScope, address, user, $"calendarSuggest.yes:{spaceId}", CancellationToken.None);

        var sent = Assert.Single(channel.SentGroupedChoices);
        Assert.Contains("milk", sent.Text);
    }

    private sealed class NeverCalledTokenVault : ITokenVault
    {
        public Task SetAsync(string secretName, string value, CancellationToken ct) => throw new NotSupportedException("Not reachable: no calendar is linked in any CalendarHandlersTests scenario.");

        public Task<string?> GetAsync(string secretName, CancellationToken ct) => throw new NotSupportedException("Not reachable: no calendar is linked in any CalendarHandlersTests scenario.");

        public Task DeleteAsync(string secretName, CancellationToken ct) => throw new NotSupportedException("Not reachable: no calendar is linked in any CalendarHandlersTests scenario.");
    }

    private sealed class NeverCalledHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("Not reachable: no calendar is linked in any CalendarHandlersTests scenario.");
    }
}
