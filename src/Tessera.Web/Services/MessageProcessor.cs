using System.Globalization;
using Microsoft.Extensions.Localization;
using Tessera.Ai.Routing;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Data;

namespace Tessera.Web.Services;

// Consumes InboundMessage from the queue. Deduplication already happened at the webhook,
// before enqueueing (docs/01-architettura.md) — this stage does not need to re-check.
public sealed class MessageProcessor(
    MessageQueue queue,
    IServiceScopeFactory scopeFactory,
    IntentRouter router,
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    ILogger<MessageProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process message {ChannelName}/{ProviderMessageId}",
                    message.ChannelName, message.ProviderMessageId);
            }
        }
    }

    private async Task ProcessAsync(InboundMessage message, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        // No HTTP context here, so nothing else sets the culture — omitting this produces
        // the silent bug where every reply comes back in English (docs/09-localizzazione.md).
        var user = message.ExternalUserId is null
            ? null
            : await identities.ResolveUserAsync(message.ChannelName, message.ExternalUserId, ct);

        var culture = new CultureInfo(user?.PreferredCulture ?? "en");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (user is null)
        {
            if (message.ExternalUserId is not null && message.Text is { } startText
                && startText.StartsWith("/start ", StringComparison.Ordinal))
            {
                await HandleLinkAsync(scope, message, startText["/start ".Length..].Trim(), ct);
                return;
            }

            logger.LogInformation(
                "Unlinked {ChannelName} identity {ExternalUserId} in chat {ExternalChatId} — culture defaulted to {Culture}: {Text}",
                message.ChannelName, message.ExternalUserId, message.ExternalChatId, culture.Name, message.Text);
            return;
        }

        logger.LogInformation(
            "Received {ChannelName} message from {DisplayName} (culture {Culture}): {Text}",
            message.ChannelName, user.DisplayName ?? user.Email, culture.Name, message.Text);

        // Full disambiguation chain (docs/02-modello-dati.md) isn't built yet — the
        // personal space created at registration is the only one a user has today.
        if (user.DefaultSpaceId is not { } spaceId)
        {
            return;
        }

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);
        var shopping = scope.ServiceProvider.GetRequiredService<ShoppingListService>();

        if (message.CallbackData is { } callbackData)
        {
            // L1 (docs/05-ottimizzazioni.md): an inline-keyboard tap is already a
            // structured action — it never goes through the intent matcher.
            await HandleCallbackAsync(shopping, address, spaceId, user.Id, callbackData, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var match = router.TryRoute(message.Text, culture.Name);

        string? reply;
        if (match is null)
        {
            // No L3/LLM fallback wired up yet — this is the honest answer until it is.
            reply = localizer["Errors.NotUnderstood"];
        }
        else
        {
            reply = match.Intent switch
            {
                "shopping.add" => await HandleAddAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.show" => await HandleShowAsync(shopping, address, spaceId, user.Id, ct),
                "shopping.check" => await HandleCheckAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.remove" => await HandleRemoveAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.clear" => await HandleClearAsync(shopping, spaceId, user.Id, ct),
                _ => null,
            };
        }

        if (reply is not null)
        {
            await channel.SendTextAsync(address, reply, ct);
        }
    }

    private async Task HandleCallbackAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, string callbackData, CancellationToken ct)
    {
        var parts = callbackData.Split(':', 2);
        if (parts.Length != 2 || parts[0] != "shopping.check" || !Guid.TryParse(parts[1], out var itemId))
        {
            return;
        }

        var item = await shopping.CheckItemByIdAsync(spaceId, userId, itemId, ct);
        if (item is null)
        {
            // Already checked by a concurrent tap/command, or the list was cleared since —
            // the button is stale. Nothing to report; the reply for the original tap
            // already dismissed the loading state (TelegramUpdateIngestor).
            return;
        }

        await channel.SendTextAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
    }

    private async Task HandleLinkAsync(AsyncServiceScope scope, InboundMessage message, string token, CancellationToken ct)
    {
        var linkService = scope.ServiceProvider.GetRequiredService<LinkService>();
        var linkedUser = await linkService.ConsumeTokenAsync(
            token, message.ChannelName, message.ExternalUserId!, message.ExternalChatId, ct);

        var culture = new CultureInfo(linkedUser?.PreferredCulture ?? "en");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var reply = linkedUser is null
            ? localizer["Link.Invalid"]
            : localizer["Link.Success", linkedUser.DisplayName ?? linkedUser.Email];

        logger.LogInformation("Link attempt for {ChannelName} identity {ExternalUserId}: {Result}",
            message.ChannelName, message.ExternalUserId, linkedUser is null ? "invalid/expired" : "success");

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);
        await channel.SendTextAsync(address, reply, ct);
    }

    private async Task<string> HandleAddAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.AddItemAsync(spaceId, userId, itemText, ct);
        return localizer["Shopping.ItemAdded", item.RawText];
    }

    private async Task<string?> HandleShowAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, ct);
        if (items.Count == 0)
        {
            return localizer["Shopping.ListEmpty"];
        }

        var lines = items.Select(i => localizer[
            i.IsChecked ? "Shopping.ListItemLineChecked" : "Shopping.ListItemLine", i.RawText].Value);
        var text = string.Join('\n', lines);

        // One button per unchecked item — a tap is a callback_query, zero interpretation,
        // zero LLM cost (docs/03-integrazioni.md, docs/05-ottimizzazioni.md).
        var choices = items
            .Where(i => !i.IsChecked)
            .Select(i => new Choice(i.RawText, $"shopping.check:{i.Id}"))
            .ToList();

        if (choices.Count == 0)
        {
            await channel.SendTextAsync(address, text, ct);
        }
        else
        {
            await channel.SendChoicesAsync(address, text, choices, ct);
        }

        return null;
    }

    private async Task<string> HandleCheckAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.CheckItemAsync(spaceId, userId, itemText, ct);
        return item is null
            ? localizer["Shopping.ItemNotFound", itemText]
            : localizer["Shopping.ItemChecked", item.RawText];
    }

    private async Task<string> HandleRemoveAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.RemoveItemAsync(spaceId, userId, itemText, ct);
        return item is null
            ? localizer["Shopping.ItemNotFound", itemText]
            : localizer["Shopping.ItemRemoved", item.RawText];
    }

    private async Task<string> HandleClearAsync(ShoppingListService shopping, Guid spaceId, Guid userId, CancellationToken ct)
    {
        await shopping.ClearAsync(spaceId, userId, ct);
        return localizer["Shopping.ListCleared"];
    }
}
