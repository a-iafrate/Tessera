using Microsoft.EntityFrameworkCore;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// Google can change a calendar's accessRole or unshare it entirely without Tessera ever
// hearing about it — nothing else calls back into the provider once a calendar is linked, so
// this periodic refresh is what keeps CalendarSpaceMapping's effective level (hard rule 15)
// from going stale (docs/02-modello-dati.md, docs/03-integrazioni.md).
public sealed class RefreshCalendarListJob(
    IServiceScopeFactory scopeFactory, ILogger<RefreshCalendarListJob> logger) : IScheduledJob
{
    public string Name => "RefreshCalendarList";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var linkedAccounts = scope.ServiceProvider.GetRequiredService<LinkedAccountService>();

        var accounts = await db.LinkedAccounts.ToListAsync(ct);
        foreach (var account in accounts)
        {
            try
            {
                await linkedAccounts.RefreshCalendarsAsync(account, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh calendar list for linked account {AccountId}", account.Id);
            }
        }
    }
}
