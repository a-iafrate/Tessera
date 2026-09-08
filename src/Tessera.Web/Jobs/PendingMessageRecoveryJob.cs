using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Channels;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// Closes the one known gap in the in-memory queue (docs/01-architettura.md, "vincolo noto:
// coda in memoria"): a restart — which happens on every deploy — between
// TelegramUpdateIngestor inserting a ProcessedMessage row and MessageProcessor marking it
// CompletedAt loses the message from MessageQueue. Once the webhook has returned 200 OK (hard
// rule 6), Telegram never redelivers it on its own, so without this job that message is simply
// gone, silently, with no error anywhere.
//
// The one-minute floor on ProcessedAt is the margin for a message still being legitimately
// handled by this same running instance — normal processing is sub-second to a few seconds
// (docs/05-ottimizzazioni.md); a row still open past a minute is either orphaned by a restart
// or stuck on a genuine bug, and either way replaying it is the right default (the alternative,
// silently dropping it, is worse).
public sealed class PendingMessageRecoveryJob(
    IServiceScopeFactory scopeFactory, MessageQueue queue, ILogger<PendingMessageRecoveryJob> logger) : IScheduledJob
{
    private static readonly TimeSpan OrphanThreshold = TimeSpan.FromMinutes(1);

    public string Name => "PendingMessageRecovery";

    // Runs on SchedulerWorker's very first tick regardless of Interval (every job does —
    // lastRun starts at DateTimeOffset.MinValue), so an orphaned message from the deploy that
    // just happened is picked up within ~30 seconds of the new instance starting, not up to 5
    // minutes later.
    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();

        // PayloadJson is null for the PayPal webhook's synchronous use of this same table
        // (docs/03-integrazioni.md) — nothing to replay there, so the filter already excludes
        // it without a channel-name special case.
        var cutoff = DateTimeOffset.UtcNow - OrphanThreshold;
        var pending = await db.ProcessedMessages
            .Where(x => x.CompletedAt == null && x.PayloadJson != null && x.ProcessedAt < cutoff)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var row in pending)
        {
            InboundMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<InboundMessage>(row.PayloadJson!);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex,
                    "Failed to deserialize orphaned message {ChannelName}/{ProviderMessageId} — dropping it, not retrying",
                    row.ChannelName, row.ProviderMessageId);
                row.CompletedAt = DateTimeOffset.UtcNow;
                continue;
            }

            if (message is null)
            {
                row.CompletedAt = DateTimeOffset.UtcNow;
                continue;
            }

            logger.LogWarning(
                "Re-enqueueing orphaned message {ChannelName}/{ProviderMessageId}, stuck since {ProcessedAt}",
                row.ChannelName, row.ProviderMessageId, row.ProcessedAt);
            await queue.EnqueueAsync(message, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}
