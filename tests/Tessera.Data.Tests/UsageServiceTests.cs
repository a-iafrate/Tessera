using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;

namespace Tessera.Data.Tests;

// The two counting-based economic guards (docs/13-piano-miglioramenti.md, D1/F4): L3 calls per
// UTC day, receipt scans per UTC calendar month. Both boundaries are computed the same way the
// production code computes them, not hardcoded dates, so these tests can never be flaky around
// midnight/month-end without also catching a real regression.
public class UsageServiceTests : IDisposable
{
    private readonly TestDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly UsageService usage;

    public UsageServiceTests()
    {
        usage = new UsageService(Db);
    }

    public void Dispose() => testDb.Dispose();

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

    // A plan with tiny limits, not the seeded Free (20 calls/day, 3 scans/month) — so a
    // boundary test needs a handful of rows, not twenty.
    private async Task<Guid> AddSpaceOnPlanAsync(int maxCallsPerDay, int maxReceiptsPerMonth)
    {
        var plan = new SubscriptionPlan
        {
            Id = Guid.NewGuid(), Name = "Test", MaxCallsPerDay = maxCallsPerDay, MaxReceiptsPerMonth = maxReceiptsPerMonth,
            MaxLinkedBots = 1, MaxLinkedCalendars = 1, HistoryMonths = 1, MaxSpacesOwned = 1,
        };
        Db.SubscriptionPlans.Add(plan);
        var space = new Space { Id = Guid.NewGuid(), Name = "Casa", OwnerId = Guid.NewGuid(), PlanId = plan.Id, CreatedAt = DateTimeOffset.UtcNow };
        Db.Spaces.Add(space);
        await Db.SaveChangesAsync();
        return space.Id;
    }

    [Fact]
    public async Task TryRecordL3CallAsync_AllowsExactlyTheDailyLimit_ThenRefuses()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 2, maxReceiptsPerMonth: 0);

        Assert.True(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));
        Assert.True(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));
        Assert.False(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));

        var countAfter = await Db.UsageEvents.CountAsync(x => x.SpaceId == spaceId);
        Assert.Equal(2, countAfter); // the refused third call recorded nothing
    }

    [Fact]
    public async Task TryRecordL3CallAsync_IgnoresEventsFromBeforeTodaysUtcBoundary()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 1, maxReceiptsPerMonth: 0);
        Db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, Kind = UsageEventKind.L3Call, OccurredAt = StartOfTodayUtc().AddSeconds(-1) });
        await Db.SaveChangesAsync();

        var allowed = await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None);

        Assert.True(allowed, "an event from yesterday must not count against today's allowance");
    }

    [Fact]
    public async Task TryRecordL3CallAsync_CountsAnEventAtExactlyTheDayBoundary()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 1, maxReceiptsPerMonth: 0);
        Db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, Kind = UsageEventKind.L3Call, OccurredAt = StartOfTodayUtc() });
        await Db.SaveChangesAsync();

        var allowed = await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None);

        Assert.False(allowed, "the boundary instant itself counts as today's usage (inclusive comparison)");
    }

    [Fact]
    public async Task TryRecordReceiptScanAsync_AllowsExactlyTheMonthlyLimit_ThenRefuses()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 0, maxReceiptsPerMonth: 1);

        Assert.True(await usage.TryRecordReceiptScanAsync(spaceId, CancellationToken.None));
        Assert.False(await usage.TryRecordReceiptScanAsync(spaceId, CancellationToken.None));
    }

    [Fact]
    public async Task TryRecordReceiptScanAsync_IgnoresEventsFromBeforeThisMonthsUtcBoundary()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 0, maxReceiptsPerMonth: 1);
        Db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), SpaceId = spaceId, Kind = UsageEventKind.ReceiptScan, OccurredAt = StartOfMonthUtc().AddSeconds(-1) });
        await Db.SaveChangesAsync();

        var allowed = await usage.TryRecordReceiptScanAsync(spaceId, CancellationToken.None);

        Assert.True(allowed, "an event from last month must not count against this month's allowance");
    }

    [Fact]
    public async Task UsageKinds_AreCountedIndependently_L3CallsDoNotConsumeTheReceiptScanAllowance()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 5, maxReceiptsPerMonth: 1);

        Assert.True(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));
        Assert.True(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));
        Assert.True(await usage.TryRecordReceiptScanAsync(spaceId, CancellationToken.None));
        Assert.False(await usage.TryRecordReceiptScanAsync(spaceId, CancellationToken.None));
        // The receipt-scan refusal above didn't touch the still-open L3 allowance.
        Assert.True(await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None));
    }

    [Fact]
    public async Task TryRecordL3CallAsync_IsIsolatedPerSpace()
    {
        var spaceA = await AddSpaceOnPlanAsync(maxCallsPerDay: 1, maxReceiptsPerMonth: 0);
        var spaceB = await AddSpaceOnPlanAsync(maxCallsPerDay: 1, maxReceiptsPerMonth: 0);

        Assert.True(await usage.TryRecordL3CallAsync(spaceA, CancellationToken.None));
        Assert.False(await usage.TryRecordL3CallAsync(spaceA, CancellationToken.None));
        // Space B's own allowance is untouched by Space A being exhausted.
        Assert.True(await usage.TryRecordL3CallAsync(spaceB, CancellationToken.None));
    }

    [Fact]
    public async Task GetTodayUsageAsync_ReportsUsedCountAndPlanLimit_WithoutRecordingAnything()
    {
        var spaceId = await AddSpaceOnPlanAsync(maxCallsPerDay: 5, maxReceiptsPerMonth: 0);
        await usage.TryRecordL3CallAsync(spaceId, CancellationToken.None);

        var (usedToday, limit, plan) = await usage.GetTodayUsageAsync(spaceId, CancellationToken.None);

        Assert.Equal(1, usedToday);
        Assert.Equal(5, limit);
        Assert.Equal(5, plan.MaxCallsPerDay);
        Assert.Equal(1, await Db.UsageEvents.CountAsync(x => x.SpaceId == spaceId)); // still just the one recorded call
    }
}
