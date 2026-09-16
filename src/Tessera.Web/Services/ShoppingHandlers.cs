using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Tessera.Core.Channels;
using Tessera.Core.Notifications;
using Tessera.Core.Resources;
using Tessera.Core.Shopping;
using Tessera.Data;

namespace Tessera.Web.Services;

// First of five domain handler classes to be extracted out of MessageProcessor
// (docs/13-piano-miglioramenti.md, F3 — one domain per commit). Shopping went first because it's
// the only one with zero cross-turn conversation state (no ConversationState.PendingIntent value
// is ever a shopping flow), which makes it the lowest-risk place to prove the extraction shape.
//
// Constructed fresh per message inside MessageProcessor.ProcessAsync, not DI-registered as a
// singleton: MessageProcessor.channel is a mutable field reassigned once per message (safe only
// because the queue drains strictly sequentially), so nothing can safely capture it at
// construction time except a per-message instance like this one.
public sealed class ShoppingHandlers(
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    Func<OnboardingService, ChannelAddress, Guid, string, string, CancellationToken, Task> finalizeReplyAsync)
{
    // Splits on commas and the culture's own word for "and" so a single slot capture ("latte e
    // pane" / "milk and bread") still adds every item named, not just the first
    // (docs/13-piano-miglioramenti.md, E1 — a voice message naming two items must add both, and
    // voice reuses this exact L2 path rather than its own). Only one undo slot ends up pointing
    // at the last item added, same as every other multi-item action in this codebase (a receipt
    // scan checking off several shopping items doesn't get one undo each either).
    public async Task<string?> AddAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo, OnboardingService onboarding,
        ChannelAddress address, Guid spaceId, Guid userId, string itemText, string? listName, CultureInfo culture, CancellationToken ct)
    {
        var addedNames = new List<string>();
        foreach (var name in SplitMultipleItems(itemText, culture))
        {
            var item = await shopping.AddItemAsync(spaceId, userId, name, listName, ct);
            await notifications.NotifyAsync(
                new ShoppingItemAdded(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
            await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, ct);
            addedNames.Add(item.RawText);
        }

        var reply = addedNames.Count == 1
            ? localizer["Shopping.ItemAdded", addedNames[0]].Value
            : localizer["Shopping.ItemsAdded", string.Join(", ", addedNames)].Value;
        await finalizeReplyAsync(onboarding, address, userId, "shopping", reply, ct);
        return null;
    }

    private static IReadOnlyList<string> SplitMultipleItems(string itemText, CultureInfo culture)
    {
        var connector = culture.TwoLetterISOLanguageName == "it" ? "e" : "and";
        return Regex.Split(itemText, $@",|\s+{connector}\s+", RegexOptions.IgnoreCase)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    public async Task<string?> ShowAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, string? listName, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, listName, ct);
        if (items.Count == 0)
        {
            return localizer["Shopping.ListEmpty"];
        }

        var (text, rows) = BuildShoppingListView(items);
        if (rows.Count == 0)
        {
            await channel.SendTextAsync(address, text, ct);
        }
        else
        {
            await channel.SendGroupedChoicesAsync(address, text, rows, ct);
        }

        return null;
    }

    // Shared by ShowAsync (first render) and the check/remove callbacks (in-place refresh) so
    // the two never drift into different renderings of the same list. A ✓/🗑 pair per unchecked
    // item, in the same row — the closest Telegram gets to "buttons beside each list line,"
    // since inline keyboards always render as a block below the text, never interleaved with it.
    private (string Text, List<IReadOnlyList<Choice>> Rows) BuildShoppingListView(IReadOnlyList<ShoppingItem> items)
    {
        if (items.Count == 0)
        {
            return (localizer["Shopping.ListEmpty"].Value, []);
        }

        var lines = items.Select(i => localizer[
            i.IsChecked ? "Shopping.ListItemLineChecked" : "Shopping.ListItemLine", i.RawText].Value);
        var text = string.Join('\n', lines);

        var rows = items
            .Where(i => !i.IsChecked)
            .Select(i => (IReadOnlyList<Choice>)new[]
            {
                new Choice(localizer["Shopping.CheckButtonLabel", i.RawText].Value, $"shopping.check:{i.Id}"),
                new Choice(localizer["Shopping.RemoveButtonLabel"].Value, $"shopping.remove:{i.Id}"),
            })
            .ToList();

        return (text, rows);
    }

    // Best-effort: no-ops if this check/remove wasn't triggered by a button tap (a text command
    // or an LLM tool call has no original list message to refresh) or if the edit itself fails.
    private async Task RefreshShoppingListMessageAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid listId,
        string? callbackMessageId, CancellationToken ct)
    {
        if (callbackMessageId is null)
        {
            return;
        }

        var items = await shopping.GetItemsByListIdAsync(spaceId, userId, listId, ct);
        var (text, rows) = BuildShoppingListView(items);
        await channel.EditListMessageAsync(address, callbackMessageId, text, rows, ct);
    }

    // Checking off an item doesn't count toward onboarding progression (docs/10-conversazione.md
    // frames it around content-creating actions — add, expense, reminder — not state changes on
    // things already there; counting every check would fire the sharing prompt after one trip
    // through the shopping list). It still gets the undo button.
    public async Task<string?> CheckAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo,
        ChannelAddress address, Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        var item = await shopping.CheckItemAsync(spaceId, userId, itemText, listName, ct);
        if (item is null)
        {
            return localizer["Shopping.ItemNotFound", itemText];
        }

        await notifications.NotifyAsync(
            new ShoppingItemChecked(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        await undo.RecordShoppingCheckAsync(userId, spaceId, item.Id, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
        return null;
    }

    public async Task<string> RemoveAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        var item = await shopping.RemoveItemAsync(spaceId, userId, itemText, listName, ct);
        return item is null
            ? localizer["Shopping.ItemNotFound", itemText]
            : localizer["Shopping.ItemRemoved", item.RawText];
    }

    public async Task<string?> ClearAsync(
        ShoppingListService shopping, UndoService undo, ChannelAddress address, Guid spaceId, Guid userId,
        string? listName, CancellationToken ct)
    {
        var cleared = await shopping.ClearAsync(spaceId, userId, listName, ct);
        await undo.RecordShoppingClearAsync(userId, spaceId, cleared, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ListCleared"], ct);
        return null;
    }

    // Generic lists beyond groceries (docs/10-conversazione.md) — ShoppingList.Name already
    // supported this; "which list?" only needs answering when the model asks about it.
    public async Task<string> ListListsAsync(ShoppingListService shopping, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var lists = await shopping.GetListsAsync(spaceId, userId, ct);
        return lists.Count == 0
            ? localizer["Shopping.NoLists"]
            : string.Join(", ", lists.Select(l => string.IsNullOrEmpty(l.Name) ? localizer["Shopping.DefaultListName"].Value : l.Name));
    }

    public async Task<string?> CorrectAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid itemId,
        string correctedText, CancellationToken ct)
    {
        var item = await shopping.CorrectItemAsync(spaceId, userId, itemId, correctedText, ct);
        if (item is null)
        {
            return localizer["Correction.Conflict"];
        }

        await SendWithUndoAsync(address, localizer["Shopping.ItemAdded", item.RawText], ct);
        return null;
    }

    public async Task HandleCheckCallbackAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo, ChannelAddress address,
        Guid spaceId, Guid userId, Guid itemId, string? callbackMessageId, CancellationToken ct)
    {
        var item = await shopping.CheckItemByIdAsync(spaceId, userId, itemId, ct);
        if (item is null)
        {
            // Already checked by a concurrent tap/command, or the list was cleared since —
            // the button is stale. Nothing to report; the reply for the original tap
            // already dismissed the loading state (TelegramUpdateIngestor).
            return;
        }

        await notifications.NotifyAsync(
            new ShoppingItemChecked(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        await undo.RecordShoppingCheckAsync(userId, spaceId, item.Id, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
        await RefreshShoppingListMessageAsync(shopping, address, spaceId, userId, item.ShoppingListId, callbackMessageId, ct);
    }

    // No undo here: removing via the list's 🗑 button matches RemoveAsync's own text-command
    // behavior, which has never offered one either — adding it would need a new
    // ShoppingRemoveUndoPayload, out of scope for what was asked (a remove button).
    public async Task HandleRemoveCallbackAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid itemId,
        string? callbackMessageId, CancellationToken ct)
    {
        var item = await shopping.RemoveItemByIdAsync(spaceId, userId, itemId, ct);
        if (item is null)
        {
            // Already removed by a concurrent tap/command — the button is stale.
            return;
        }

        await channel.SendTextAsync(address, localizer["Shopping.ItemRemoved", item.RawText], ct);
        await RefreshShoppingListMessageAsync(shopping, address, spaceId, userId, item.ShoppingListId, callbackMessageId, ct);
    }

    private async Task SendWithUndoAsync(ChannelAddress address, string text, CancellationToken ct)
    {
        var choices = new[] { new Choice(localizer["Undo.Button"].Value, "undo:tap") };
        await channel.SendChoicesAsync(address, text, choices, ct);
    }
}
