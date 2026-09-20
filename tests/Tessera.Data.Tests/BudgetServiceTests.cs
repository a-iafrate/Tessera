using Tessera.Core.Abstractions;
using Tessera.Core.Reminders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Tests;

// GetMonthlyForecastAsync (docs/13-piano-miglioramenti.md, E7) — the one BudgetService method
// with no existing test coverage at all before this (F4 didn't cover BudgetService; it's tested
// indirectly through ExpenseHandlersTests for everything else it does).
public class BudgetServiceTests : IDisposable
{
    private readonly TestDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly ExpenseService expenses;
    private readonly RecurringExpenseService recurringExpenses;
    private readonly BudgetService budgets;

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

    public BudgetServiceTests()
    {
        var accessPolicy = new AllowAllAccessPolicy();
        expenses = new ExpenseService(Db, accessPolicy, testDb.Cache);
        recurringExpenses = new RecurringExpenseService(Db, accessPolicy);
        budgets = new BudgetService(Db, accessPolicy, expenses, recurringExpenses);

        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    [Fact]
    public async Task GetMonthlyForecastAsync_ReturnsNull_WhenThereAreNoActiveRecurringExpenses()
    {
        var result = await budgets.GetMonthlyForecastAsync(spaceId, userId, today, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMonthlyForecastAsync_ReturnsNull_WhenOnlyAReminderOnlyRecurringIsActive()
    {
        await recurringExpenses.CreateAsync(spaceId, userId, 50m, "Electricity bill", RecurrenceFrequency.Monthly, autoRegister: false, CancellationToken.None);

        var result = await budgets.GetMonthlyForecastAsync(spaceId, userId, today, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMonthlyForecastAsync_ReturnsNull_WhenTheOnlyAutoRegisterRecurringAlreadyFiredThisMonth()
    {
        var recurring = await recurringExpenses.CreateAsync(spaceId, userId, 20m, "Netflix", RecurrenceFrequency.Monthly, autoRegister: true, CancellationToken.None);
        await recurringExpenses.GenerateAsync(recurring.Id, today, CancellationToken.None);

        var result = await budgets.GetMonthlyForecastAsync(spaceId, userId, today, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMonthlyForecastAsync_ProjectsPendingRecurring_PlusCurrentSpend()
    {
        // 30 recorded by hand, plus 20 landed automatically when the already-fired recurring
        // below generated its own Expense — both count toward SpentSoFar.
        await expenses.RecordAsync(spaceId, userId, 30m, categoryId: null, merchant: null, today, note: null, CancellationToken.None);
        var fired = await recurringExpenses.CreateAsync(spaceId, userId, 20m, "Netflix", RecurrenceFrequency.Monthly, autoRegister: true, CancellationToken.None);
        await recurringExpenses.GenerateAsync(fired.Id, today, CancellationToken.None);
        await recurringExpenses.CreateAsync(spaceId, userId, 15m, "Rent top-up", RecurrenceFrequency.Monthly, autoRegister: true, CancellationToken.None);

        var result = await budgets.GetMonthlyForecastAsync(spaceId, userId, today, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(50m, result.SpentSoFar);
        Assert.Equal(15m, result.PendingRecurring);
        Assert.Equal(65m, result.ProjectedTotal);
    }

    private sealed class AllowAllAccessPolicy : IAccessPolicy
    {
        public Task<bool> CanAsync(Guid userId, Guid spaceId, ResourceKind resource, AccessLevel required, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
