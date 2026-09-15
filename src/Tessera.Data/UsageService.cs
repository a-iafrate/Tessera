using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// The counting-based economic guards tied to SubscriptionPlan (docs/04-costi.md,
// docs/13-piano-miglioramenti.md D1) — L3/LLM calls per day and receipt scans per month. Linked-
// bot limits, spaces-owned limits, and calendar limits are enforced elsewhere (LinkService,
// SpaceService, CalendarSpaceService) since they're not "how many times", just "how many at
// once". L1/L2 native commands and matchers never touch this: they cost nothing and stay
// available even once a space has used up its daily L3 allowance.
//
// TelemetryClient optional, defaulting to null when Application Insights isn't configured
// (Program.cs) — same shape MessageProcessor/LlmFallbackClient already use, extended here to
// Tessera.Data because this is the one place that actually knows the plan and the count, so a
// single TrackEvent here covers all three call sites in MessageProcessor instead of tracking
// the same thing three times with whatever's in scope at each one (docs/13-piano-miglioramenti.md, D3).
public sealed class UsageService(TesseraDbContext db, TelemetryClient? telemetry = null)
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
        var usedToday = await db.UsageEvents.CountAsync(
            x => x.SpaceId == spaceId && x.Kind == UsageEventKind.L3Call && x.OccurredAt >= todayStart, ct);
        if (usedToday >= plan.MaxCallsPerDay)
        {
            telemetry?.TrackEvent("UsageLimitReached", new Dictionary<string, string>
            {
                ["SpaceId"] = spaceId.ToString(),
                ["PlanName"] = plan.Name,
                ["Limit"] = plan.MaxCallsPerDay.ToString(),
            });
            return false;
        }

        db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, OccurredAt = DateTimeOffset.UtcNow, Kind = UsageEventKind.L3Call });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Same check-and-record shape as TryRecordL3CallAsync, but per calendar month instead of
    // per day — a receipt scan is a real per-call vision API cost (docs/04-costi.md), so a free
    // space gets a handful a month rather than the all-or-nothing AllowsReceiptScanning this
    // replaces (docs/13-piano-miglioramenti.md, D1).
    public async Task<bool> TryRecordReceiptScanAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        var monthStart = StartOfMonthUtc();
        var usedThisMonth = await db.UsageEvents.CountAsync(
            x => x.SpaceId == spaceId && x.Kind == UsageEventKind.ReceiptScan && x.OccurredAt >= monthStart, ct);
        if (usedThisMonth >= plan.MaxReceiptsPerMonth)
        {
            telemetry?.TrackEvent("UsageLimitReached", new Dictionary<string, string>
            {
                ["SpaceId"] = spaceId.ToString(),
                ["PlanName"] = plan.Name,
                ["Limit"] = plan.MaxReceiptsPerMonth.ToString(),
                ["Kind"] = "ReceiptScan",
            });
            return false;
        }

        db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, OccurredAt = DateTimeOffset.UtcNow, Kind = UsageEventKind.ReceiptScan });
        await db.SaveChangesAsync(ct);
        return true;
    }

    // For the console usage page — read-only, no recording.
    public async Task<(int UsedToday, int Limit, SubscriptionPlan Plan)> GetTodayUsageAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        var todayStart = StartOfTodayUtc();
        var usedToday = await db.UsageEvents.CountAsync(
            x => x.SpaceId == spaceId && x.Kind == UsageEventKind.L3Call && x.OccurredAt >= todayStart, ct);
        return (usedToday, plan.MaxCallsPerDay, plan);
    }

    // Same shape as GetTodayUsageAsync, for the receipt-scan allowance instead.
    public async Task<(int UsedThisMonth, int Limit)> GetReceiptUsageAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        var monthStart = StartOfMonthUtc();
        var usedThisMonth = await db.UsageEvents.CountAsync(
            x => x.SpaceId == spaceId && x.Kind == UsageEventKind.ReceiptScan && x.OccurredAt >= monthStart, ct);
        return (usedThisMonth, plan.MaxReceiptsPerMonth);
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

    private static DateTimeOffset StartOfMonthUtc()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
