using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// The only economic protection tied to SubscriptionPlan today (docs/04-costi.md) — everything
// else about plans (linked-bot limits, payment, upgrade flow) is still deliberately
// unenforced. Only L3/LLM calls count: L1/L2 native commands and matchers cost nothing and
// stay available even once a space has used up its daily allowance.
public sealed class UsageService(TesseraDbContext db)
{
    // Single method that checks-and-records in one round trip, since a caller only ever wants
    // "was I allowed to make this call" — a separate check-then-record pair would just be two
    // queries for the same decision, with a race window in between for no benefit (this app has
    // no meaningful concurrent-L3-calls-for-the-same-space scenario to protect against anyway).
    public async Task<bool> TryRecordL3CallAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        var todayStart = StartOfTodayUtc();
        var usedToday = await db.UsageEvents.CountAsync(x => x.SpaceId == spaceId && x.OccurredAt >= todayStart, ct);
        if (usedToday >= plan.MaxCallsPerDay)
        {
            return false;
        }

        db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // For the console usage page — read-only, no recording.
    public async Task<(int UsedToday, int Limit, SubscriptionPlan Plan)> GetTodayUsageAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        var todayStart = StartOfTodayUtc();
        var usedToday = await db.UsageEvents.CountAsync(x => x.SpaceId == spaceId && x.OccurredAt >= todayStart, ct);
        return (usedToday, plan.MaxCallsPerDay, plan);
    }

    // For the public pricing page — the only other place a SubscriptionPlan row gets read.
    // Ordered by price so cheapest-first matches how a pricing page is normally laid out.
    public async Task<IReadOnlyList<SubscriptionPlan>> GetAllPlansAsync(CancellationToken ct) =>
        await db.SubscriptionPlans.AsNoTracking().OrderBy(x => x.MonthlyPrice).ToListAsync(ct);

    // UTC, not the requesting user's own timezone: the limit is per-space, and a space can have
    // members in different zones — there's no single "midnight" that's correct for all of them,
    // so this picks the one that's at least consistent and unambiguous.
    private static DateTimeOffset StartOfTodayUtc()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
