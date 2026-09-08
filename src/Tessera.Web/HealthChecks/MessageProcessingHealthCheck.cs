using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tessera.Data;

namespace Tessera.Web.HealthChecks;

// Reports Degraded when a Telegram message has sat unprocessed for longer than
// PendingMessageRecoveryJob's own orphan threshold — the signal that the queue is actually
// stuck, not just idle. Idle is normal for a personal/family bot (docs/06-roadmap.md): no
// traffic overnight isn't a health problem, so this deliberately doesn't look at "time since
// last message" at all, only at whether anything is stuck open right now.
public sealed class MessageProcessingHealthCheck(TesseraDbContext db) : IHealthCheck
{
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - StuckThreshold;
        var stuckCount = await db.ProcessedMessages
            .CountAsync(x => x.CompletedAt == null && x.PayloadJson != null && x.ProcessedAt < cutoff, ct);

        return stuckCount == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"{stuckCount} message(s) have been unprocessed for more than {StuckThreshold.TotalMinutes:0} minutes.");
    }
}
