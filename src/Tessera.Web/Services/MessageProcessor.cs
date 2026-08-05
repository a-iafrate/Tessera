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
        if (user.DefaultSpaceId is not { } spaceId || string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var shopping = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
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
                "shopping.show" => await HandleShowAsync(shopping, spaceId, user.Id, ct),
                "shopping.check" => await HandleCheckAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.remove" => await HandleRemoveAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.clear" => await HandleClearAsync(shopping, spaceId, user.Id, ct),
                _ => null,
            };
        }

        if (reply is not null)
        {
            var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);
            await channel.SendTextAsync(address, reply, ct);
        }
    }

    private async Task<string> HandleAddAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.AddItemAsync(spaceId, userId, itemText, ct);
        return localizer["Shopping.ItemAdded", item.RawText];
    }

    private async Task<string> HandleShowAsync(ShoppingListService shopping, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, ct);
        if (items.Count == 0)
        {
            return localizer["Shopping.ListEmpty"];
        }

        var lines = items.Select(i => localizer[
            i.IsChecked ? "Shopping.ListItemLineChecked" : "Shopping.ListItemLine", i.RawText].Value);
        return string.Join('\n', lines);
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
