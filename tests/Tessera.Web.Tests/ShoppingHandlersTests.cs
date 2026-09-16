using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// First extracted domain handler (docs/13-piano-miglioramenti.md, F3) — these tests are the
// point of the extraction: none of this logic had a single test while it lived inside
// MessageProcessor. Uses the real ShoppingListService/UndoService/OnboardingService/
// NotificationService against an in-memory SQLite database (same TestWebDatabase shape as
// tests/Tessera.Data.Tests), and a hand-written FakeChannel to assert what was actually sent —
// no mock framework, matching every other test project in this solution.
public class ShoppingHandlersTests : IDisposable
{
    private readonly TestWebDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly ShoppingListService shopping;
    private readonly NotificationService notifications;
    private readonly UndoService undo;
    private readonly OnboardingService onboarding;
    private readonly FakeChannel channel = new();
    private readonly IStringLocalizer<Messages> localizer;
    private readonly List<(Guid UserId, string FeatureKey, string BaseReply)> finalizedReplies = [];

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly ChannelAddress address = new("fake", "chat-1");

    public ShoppingHandlersTests()
    {
        shopping = new ShoppingListService(Db, new TestWebDatabase.AllowAllAccessPolicy());
        undo = new UndoService(Db);
        onboarding = new OnboardingService(Db);
        localizer = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();
        notifications = new NotificationService(
            Db, new FakeChannelIdentityRepository(), new ActorNameResolver(Db), new FakeChannelRegistry(), new NotificationAggregator(), localizer);

        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    private ShoppingHandlers CreateHandlers() => new(channel, localizer, (o, a, u, featureKey, baseReply, ct) =>
    {
        finalizedReplies.Add((u, featureKey, baseReply));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task AddAsync_AddsOneItem_AndFinalizesASingularReply()
    {
        var handlers = CreateHandlers();

        var result = await handlers.AddAsync(shopping, notifications, undo, onboarding, address, spaceId, userId, "latte", listName: null, culture: System.Globalization.CultureInfo.InvariantCulture, CancellationToken.None);

        Assert.Null(result); // already sent via finalizeReplyAsync
        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None);
        Assert.Single(items);
        Assert.Equal("latte", items[0].RawText);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("shopping", finalized.FeatureKey);
        Assert.Equal("Added latte", finalized.BaseReply);
    }

    [Fact]
    public async Task AddAsync_SplitsOnTheCulturesConnector_AddingEveryItemNamed()
    {
        var handlers = CreateHandlers();
        var italian = new System.Globalization.CultureInfo("it");

        await handlers.AddAsync(shopping, notifications, undo, onboarding, address, spaceId, userId, "latte e pane", listName: null, italian, CancellationToken.None);

        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.RawText == "latte");
        Assert.Contains(items, i => i.RawText == "pane");
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("Added latte, pane", finalized.BaseReply);
    }

    [Fact]
    public async Task ShowAsync_ReturnsEmptyMessage_WithoutSendingAnything_WhenListIsEmpty()
    {
        var handlers = CreateHandlers();

        var result = await handlers.ShowAsync(shopping, address, spaceId, userId, listName: null, CancellationToken.None);

        Assert.Equal("The list is empty", result);
        Assert.Empty(channel.SentTexts);
        Assert.Empty(channel.SentGroupedChoices);
    }

    [Fact]
    public async Task ShowAsync_SendsGroupedChoices_WithACheckAndRemoveButtonPerUncheckedItem()
    {
        var handlers = CreateHandlers();
        await shopping.AddItemAsync(spaceId, userId, "latte", listName: null, CancellationToken.None);

        var result = await handlers.ShowAsync(shopping, address, spaceId, userId, listName: null, CancellationToken.None);

        Assert.Null(result); // already sent
        var sent = Assert.Single(channel.SentGroupedChoices);
        var row = Assert.Single(sent.Rows);
        Assert.Equal(2, row.Count);
        Assert.Equal("shopping.check:", row[0].Value[..15]);
        Assert.StartsWith("shopping.remove:", row[1].Value);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNotFound_WhenNoItemMatches()
    {
        var handlers = CreateHandlers();

        var result = await handlers.CheckAsync(shopping, notifications, undo, address, spaceId, userId, "latte", listName: null, CancellationToken.None);

        Assert.Equal("I couldn't find \"latte\" in the list", result);
        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task CheckAsync_ChecksTheItem_AndSendsWithAnUndoButton()
    {
        var handlers = CreateHandlers();
        await shopping.AddItemAsync(spaceId, userId, "latte", listName: null, CancellationToken.None);

        var result = await handlers.CheckAsync(shopping, notifications, undo, address, spaceId, userId, "latte", listName: null, CancellationToken.None);

        Assert.Null(result);
        var sent = Assert.Single(channel.SentChoices);
        Assert.Equal("Checked off latte", sent.Text);
        Assert.Single(sent.Choices); // the undo button
        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None);
        Assert.True(items.Single().IsChecked);
    }

    [Fact]
    public async Task RemoveAsync_ReturnsTheRemovedItemsName_AndNeverSendsAnything()
    {
        var handlers = CreateHandlers();
        await shopping.AddItemAsync(spaceId, userId, "latte", listName: null, CancellationToken.None);

        var result = await handlers.RemoveAsync(shopping, spaceId, userId, "latte", listName: null, CancellationToken.None);

        Assert.Equal("Removed latte", result);
        Assert.Empty(channel.SentTexts);
        Assert.Empty(channel.SentChoices);
        Assert.Empty(await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None));
    }

    [Fact]
    public async Task ClearAsync_RemovesEveryItem_AndRecordsOneBulkUndo()
    {
        var handlers = CreateHandlers();
        await shopping.AddItemAsync(spaceId, userId, "latte", listName: null, CancellationToken.None);
        await shopping.AddItemAsync(spaceId, userId, "pane", listName: null, CancellationToken.None);

        var result = await handlers.ClearAsync(shopping, undo, address, spaceId, userId, listName: null, CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None));
        var undoResult = await undo.TryUndoLastAsync(userId, CancellationToken.None);
        var succeeded = Assert.IsType<UndoSucceeded>(undoResult);
        Assert.Equal("shopping.clear", succeeded.OperationType);
        Assert.Equal(2, (await shopping.GetItemsAsync(spaceId, userId, listName: null, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task HandleCheckCallbackAsync_IsStaleTapSafe_WhenTheItemIsAlreadyGone()
    {
        var handlers = CreateHandlers();

        // No exception, no send — the button is stale (item never existed / already handled).
        await handlers.HandleCheckCallbackAsync(shopping, notifications, undo, address, spaceId, userId, Guid.NewGuid(), callbackMessageId: null, CancellationToken.None);

        Assert.Empty(channel.SentChoices);
    }

    [Fact]
    public async Task HandleRemoveCallbackAsync_RemovesById_AndRefreshesTheOriginalMessageWhenPresent()
    {
        var handlers = CreateHandlers();
        var item = await shopping.AddItemAsync(spaceId, userId, "latte", listName: null, CancellationToken.None);

        await handlers.HandleRemoveCallbackAsync(shopping, address, spaceId, userId, item.Id, callbackMessageId: "msg-1", CancellationToken.None);

        var sent = Assert.Single(channel.SentTexts);
        Assert.Equal("Removed latte", sent.Text);
        var edited = Assert.Single(channel.EditedListMessages);
        Assert.Equal("msg-1", edited.MessageId);
        Assert.Equal("The list is empty", edited.Text);
    }

    private sealed class FakeChannelIdentityRepository : IChannelIdentityRepository
    {
        public Task<User?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct) =>
            Task.FromResult<User?>(null);

        public Task<IReadOnlyList<ChannelIdentity>> GetForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChannelIdentity>>([]);
    }

    private sealed class FakeChannelRegistry : IChannelRegistry
    {
        public IChannel? TryGet(string channelName) => null;
    }
}
