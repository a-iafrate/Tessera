using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Expenses;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class BudgetService(TesseraDbContext db, IAccessPolicy accessPolicy, ExpenseService expenses)
{
    public async Task<Budget> SetAsync(
        Guid spaceId, Guid userId, Guid? categoryId, decimal monthlyLimit, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var budget = await db.Budgets.FirstOrDefaultAsync(x => x.SpaceId == spaceId && x.CategoryId == categoryId, ct);
        if (budget is null)
        {
            budget = new Budget { Id = Guid.NewGuid(), SpaceId = spaceId, CategoryId = categoryId };
            db.Budgets.Add(budget);
        }

        budget.MonthlyLimit = monthlyLimit;
        await db.SaveChangesAsync(ct);
        return budget;
    }

    public async Task<IReadOnlyList<Budget>> GetActiveAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        return await db.Budgets
            .Where(x => x.SpaceId == spaceId)
            .OrderBy(x => x.CategoryId)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // Reactive check "dopo ogni spesa" (docs/01-architettura.md) — the timed BudgetAlertJob
    // isn't built yet, so this runs synchronously right after ExpenseService.RecordAsync.
    // LastAlertedFor caps it at one alert per budget per month, not per expense.
    public async Task<IReadOnlyList<BudgetAlert>> CheckThresholdsAsync(
        Guid spaceId, Guid userId, Guid? expenseCategoryId, DateOnly expenseDate, CancellationToken ct)
    {
        var candidateCategoryIds = new List<Guid?> { null };
        if (expenseCategoryId is { } categoryId)
        {
            candidateCategoryIds.Add(categoryId);
        }

        var budgets = await db.Budgets
            .Where(x => x.SpaceId == spaceId && candidateCategoryIds.Contains(x.CategoryId))
            .ToListAsync(ct);

        var currentMonth = new DateOnly(expenseDate.Year, expenseDate.Month, 1);
        var alerts = new List<BudgetAlert>();

        foreach (var budget in budgets)
        {
            if (budget.LastAlertedFor == currentMonth)
            {
                continue;
            }

            var (spent, _) = budget.CategoryId is { } budgetCategoryId
                ? await expenses.GetCategoryTotalAsync(spaceId, userId, budgetCategoryId, expenseDate.Year, expenseDate.Month, ct)
                : await expenses.GetMonthlyTotalAsync(spaceId, userId, expenseDate.Year, expenseDate.Month, ct);

            var threshold = budget.MonthlyLimit * budget.AlertThresholdPercent / 100m;
            if (spent < threshold)
            {
                continue;
            }

            budget.LastAlertedFor = currentMonth;
            alerts.Add(new BudgetAlert(budget.CategoryId, spent, budget.MonthlyLimit));
        }

        if (alerts.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return alerts;
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.Expenses, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to Expenses in space {spaceId}.");
        }
    }
}
