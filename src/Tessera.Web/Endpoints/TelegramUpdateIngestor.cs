using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Tessera.Core.Channels;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Endpoints;

// Shared by both ingress paths — the production webhook and the development-only long
// polling receiver — so deduplication logic can't drift between the two (docs/08-setup-sviluppo.md).
public sealed class TelegramUpdateIngestor(TesseraDbContext db, MessageQueue queue, ITelegramBotClient client)
{
    public async Task<bool> IngestAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            // Dismiss the button's loading spinner immediately — the actual state change
            // happens asynchronously in MessageProcessor (docs/05-ottimizzazioni.md, L1).
            try
            {
                await client.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            }
            catch (Exception)
            {
                // Telegram rejects this once the callback is >~a few seconds old (e.g. a
                // retried update); the check/reply still happens below regardless.
            }
        }

        var inbound = update.ToInbound();
        if (inbound is null)
        {
            return false;
        }

        var alreadyProcessed = await db.ProcessedMessages.AnyAsync(
            x => x.ChannelName == inbound.ChannelName && x.ProviderMessageId == inbound.ProviderMessageId, ct);
        if (alreadyProcessed)
        {
            return false;
        }

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ChannelName = inbound.ChannelName,
            ProviderMessageId = inbound.ProviderMessageId,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(inbound, ct);
        return true;
    }
}
