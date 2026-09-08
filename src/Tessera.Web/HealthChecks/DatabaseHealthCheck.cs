using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tessera.Data;

namespace Tessera.Web.HealthChecks;

// A hand-rolled check instead of the AddDbContextCheck<T> package
// (Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore) — the same "avoid an SDK
// dependency for something a few lines of code already do" preference used for the OAuth
// clients (docs/06-roadmap.md) applies here too, and CanConnectAsync is all this needs.
public sealed class DatabaseHealthCheck(TesseraDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database.CanConnectAsync returned false.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Failed to connect to the database.", ex);
        }
    }
}
