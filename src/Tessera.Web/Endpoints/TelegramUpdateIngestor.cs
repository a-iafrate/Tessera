using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Tessera.Core.Channels;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Endpoints;

// Shared by both ingress paths — the production webhook and the development-only long
// polling receiver — so deduplication logic can't drift between the two (docs/08-setup-sviluppo.md).
public sealed class TelegramUpdateIngestor(TesseraDbContext db, MessageQueue queue)
{
    public async Task<bool> IngestAsync(Update update, CancellationToken ct)
    {
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
