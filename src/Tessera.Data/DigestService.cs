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
// authorization logic of its own — a domain the caller lacks Read on just contributes nothing,
// via TryReadAsync below, rather than failing the whole digest. That matters now that
// DailyDigestJob calls this once per space a user belongs to, not just one they're guaranteed
// full access to (docs/13-piano-miglioramenti.md, E4) — a member with, say, ShoppingList-only
// permission in a space shouldn't blow up their digest for every other space they're also in.
// CalendarQueryService is resolved from the container rather than constructor-injected because
// it's only registered when a calendar provider is configured (Program.cs) — same
// optional-dependency shape as AttachmentService elsewhere.
public sealed class DigestService(
    ReminderService reminders, ShoppingListService shopping, BudgetService budgets, IServiceProvider serviceProvider)
{
    public async Task<DailyDigest> BuildAsync(
        Guid spaceId, Guid userId, TimeZoneInfo timeZone, DateOnly today, CancellationToken ct)
    {
        var remindersToday = await TryReadAsync(() => GetRemindersTodayAsync(spaceId, userId, timeZone, today, ct));
        var eventsToday = await TryReadAsync(() => GetEventsTodayAsync(spaceId, userId, timeZone, today, ct));
        var missingItems = await TryReadAsync(() => GetMissingItemsAsync(spaceId, userId, ct));
        var budgetStatuses = await TryReadAsync(() => budgets.GetStatusAsync(spaceId, userId, today.Year, today.Month, ct));

        return new DailyDigest(remindersToday, eventsToday, missingItems, budgetStatuses);
    }

    private async Task<IReadOnlyList<Reminder>> GetRemindersTodayAsync(
        Guid spaceId, Guid userId, TimeZoneInfo timeZone, DateOnly today, CancellationToken ct)
    {
        var pending = await reminders.GetPendingAsync(spaceId, userId, ct);
        return pending
            .Where(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.DueAt, timeZone).DateTime) == today)
            .ToList();
    }

    private async Task<IReadOnlyList<ShoppingItem>> GetMissingItemsAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, ct);
        return items.Where(i => !i.IsChecked).ToList();
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

    private static async Task<IReadOnlyList<T>> TryReadAsync<T>(Func<Task<IReadOnlyList<T>>> query)
    {
        try
        {
            return await query();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
