using Microsoft.EntityFrameworkCore;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// ProcessedMessages has no natural cap otherwise (docs/05-ottimizzazioni.md's "don't keep what
// nothing reads" applied here) — a dedup row only needs to exist for as long as
// PendingMessageRecoveryJob might still replay it, measured in minutes, not indefinitely.
public sealed class ProcessedMessagePurgeJob(IServiceScopeFactory scopeFactory) : IScheduledJob
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public string Name => "ProcessedMessagePurge";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();

        var cutoff = DateTimeOffset.UtcNow - Retention;

        // Two different notions of "done", both older than the retention window:
        // - CompletedAt set (Telegram, via MessageProcessor) — the ordinary case.
        // - PayloadJson null (PayPal, docs/03-integrazioni.md) — that webhook processes
        //   synchronously within the request and never sets CompletedAt at all, so ProcessedAt
        //   itself is the only completion signal it has.
        await db.ProcessedMessages
            .Where(x => (x.CompletedAt != null && x.CompletedAt < cutoff)
                || (x.PayloadJson == null && x.ProcessedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }
}
