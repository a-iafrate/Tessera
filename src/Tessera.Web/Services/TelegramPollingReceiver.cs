using Telegram.Bot;
using Tessera.Web.Endpoints;

namespace Tessera.Web.Services;

// Development-only alternative to the webhook (docs/08-setup-sviluppo.md): the bot asks
// Telegram for updates instead of Telegram calling us, so there is no tunnel (ngrok) to
// manage and the debugger attaches normally. Never runs outside Development — the dev
// bot has its own BotFather token, kept separate from the one used by the deployed webhook.
public sealed class TelegramPollingReceiver(
    ITelegramBotClient client, IServiceScopeFactory scopeFactory, ILogger<TelegramPollingReceiver> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram long polling started.");
        var offset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            Telegram.Bot.Types.Update[] updates;
            try
            {
                updates = await client.GetUpdates(offset: offset, timeout: 30, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling request failed, retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;

                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestor = scope.ServiceProvider.GetRequiredService<TelegramUpdateIngestor>();
                await ingestor.IngestAsync(update, stoppingToken);
            }
        }
    }
}
