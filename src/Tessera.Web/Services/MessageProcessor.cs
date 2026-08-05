using Tessera.Core.Channels;

namespace Tessera.Web.Services;

// Consumes InboundMessage from the queue. Deduplication already happened at the webhook,
// before enqueueing (docs/01-architettura.md) — this stage does not need to re-check.
//
// Identity resolution, culture setup and intent routing (docs/05-ottimizzazioni.md,
// docs/09-localizzazione.md) land here in a later step; for now this only logs receipt.
public sealed class MessageProcessor(MessageQueue queue, ILogger<MessageProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation(
                    "Received {ChannelName} message from chat {ExternalChatId}: {Text}",
                    message.ChannelName, message.ExternalChatId, message.Text);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to process message {ChannelName}/{ProviderMessageId}",
                    message.ChannelName, message.ProviderMessageId);
            }
        }
    }
}
