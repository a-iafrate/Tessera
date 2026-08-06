using Tessera.Core.Expenses;
using Tessera.Core.Reminders;
using Tessera.Core.Shopping;

namespace Tessera.Data;

public sealed record DailyDigest(
    IReadOnlyList<Reminder> RemindersToday,
    IReadOnlyList<ShoppingItem> MissingItems,
    IReadOnlyList<BudgetStatus> BudgetStatuses);

// Composes across three domains for the daily digest (docs/06-roadmap.md): today's
// reminders, what's missing from the shopping list, and budget status. Each underlying
// service still runs its own access check, so this adds no authorization logic of its own.
public sealed class DigestService(ReminderService reminders, ShoppingListService shopping, BudgetService budgets)
{
    public async Task<DailyDigest> BuildAsync(
        Guid spaceId, Guid userId, TimeZoneInfo timeZone, DateOnly today, CancellationToken ct)
    {
        var pending = await reminders.GetPendingAsync(spaceId, userId, ct);
        var remindersToday = pending
            .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.DueAt, timeZone).DateTime) == today)
            .ToList();

        var items = await shopping.GetItemsAsync(spaceId, userId, ct);
        var missingItems = items.Where(i => !i.IsChecked).ToList();

        var budgetStatuses = await budgets.GetStatusAsync(spaceId, userId, today.Year, today.Month, ct);

        return new DailyDigest(remindersToday, missingItems, budgetStatuses);
    }
}
