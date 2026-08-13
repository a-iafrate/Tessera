using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Calendars;
using Tessera.Core.Expenses;
using Tessera.Core.Reminders;
using Tessera.Core.Shopping;

namespace Tessera.Data;

public sealed record DailyDigest(
    IReadOnlyList<Reminder> RemindersToday,
    IReadOnlyList<CalendarEventInfo> EventsToday,
    IReadOnlyList<ShoppingItem> MissingItems,
    IReadOnlyList<BudgetStatus> BudgetStatuses);

// Composes across four domains for the daily digest (docs/06-roadmap.md): today's
// reminders, today's calendar appointments, what's missing from the shopping list, and
// budget status. Each underlying service still runs its own access check, so this adds no
// authorization logic of its own. CalendarQueryService is resolved from the container rather
// than constructor-injected because it's only registered when a calendar provider is
// configured (Program.cs) — same optional-dependency shape as AttachmentService elsewhere.
public sealed class DigestService(
    ReminderService reminders, ShoppingListService shopping, BudgetService budgets, IServiceProvider serviceProvider)
{
    public async Task<DailyDigest> BuildAsync(
        Guid spaceId, Guid userId, TimeZoneInfo timeZone, DateOnly today, CancellationToken ct)
    {
        var pending = await reminders.GetPendingAsync(spaceId, userId, ct);
        var remindersToday = pending
            .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.DueAt, timeZone).DateTime) == today)
            .ToList();

        var eventsToday = await GetEventsTodayAsync(spaceId, userId, timeZone, today, ct);

        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, ct);
        var missingItems = items.Where(i => !i.IsChecked).ToList();

        var budgetStatuses = await budgets.GetStatusAsync(spaceId, userId, today.Year, today.Month, ct);

        return new DailyDigest(remindersToday, eventsToday, missingItems, budgetStatuses);
    }

    private async Task<IReadOnlyList<CalendarEventInfo>> GetEventsTodayAsync(
        Guid spaceId, Guid userId, TimeZoneInfo timeZone, DateOnly today, CancellationToken ct)
    {
        var calendars = serviceProvider.GetService<CalendarQueryService>();
        if (calendars is null)
        {
            return [];
        }

        var todayStartLocal = today.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(todayStartLocal, timeZone.GetUtcOffset(todayStartLocal));
        return await calendars.GetEventsAsync(spaceId, userId, from, from.AddDays(1), ct);
    }
}
