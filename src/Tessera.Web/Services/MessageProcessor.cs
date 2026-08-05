using System.Globalization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;

namespace Tessera.Web.Services;

// Consumes InboundMessage from the queue. Deduplication already happened at the webhook,
// before enqueueing (docs/01-architettura.md) — this stage does not need to re-check.
//
// Intent routing and domain handlers (shopping list, expenses, ...) land here in a later
// step; for now this resolves identity, sets the culture explicitly, and logs receipt.
public sealed class MessageProcessor(
    MessageQueue queue, IServiceScopeFactory scopeFactory, ILogger<MessageProcessor> logger) : BackgroundService
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
        }
        else
        {
            logger.LogInformation(
                "Received {ChannelName} message from {DisplayName} (culture {Culture}): {Text}",
                message.ChannelName, user.DisplayName ?? user.Email, culture.Name, message.Text);
        }
    }
}
