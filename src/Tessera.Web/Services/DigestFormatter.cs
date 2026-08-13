using System.Globalization;
using Microsoft.Extensions.Localization;
using Tessera.Core.Expenses;
using Tessera.Core.Resources;
using Tessera.Data;

namespace Tessera.Web.Services;

// Shared by the on-demand /digest command and the proactive DailyDigestJob, so the two
// can't drift into different wording for the same content (docs/06-roadmap.md).
public static class DigestFormatter
{
    public static string Format(
        DailyDigest daily, IReadOnlyList<Category> categories, string currency,
        TimeZoneInfo timeZone, CultureInfo culture, IStringLocalizer<Messages> localizer)
    {
        var remindersSection = daily.RemindersToday.Count == 0
            ? localizer["Digest.RemindersEmpty"].Value
            : string.Join('\n', daily.RemindersToday.Select(r =>
                localizer["Reminders.ListItemLine", MessageProcessor.FormatDueAt(r.DueAt, timeZone, culture), r.Text].Value));

        var calendarSection = daily.EventsToday.Count == 0
            ? localizer["Digest.CalendarEmpty"].Value
            : string.Join('\n', daily.EventsToday.Select(e =>
                localizer["Calendars.EventLine", MessageProcessor.FormatDueAt(e.Start, timeZone, culture), e.Title].Value));

        var shoppingSection = daily.MissingItems.Count == 0
            ? localizer["Digest.ShoppingEmpty"].Value
            : string.Join('\n', daily.MissingItems.Select(i => localizer["Shopping.ListItemLine", i.RawText].Value));

        var budgetSection = daily.BudgetStatuses.Count == 0
            ? localizer["Budget.ListEmpty"].Value
            : string.Join('\n', daily.BudgetStatuses.Select(status =>
            {
                var spentFormatted = MoneyFormatter.Format(status.Spent, currency, culture.Name);
                var limitFormatted = MoneyFormatter.Format(status.Limit, currency, culture.Name);
                if (status.CategoryId is null)
                {
                    return localizer["Digest.BudgetLineOverall", spentFormatted, limitFormatted].Value;
                }

                var category = categories.FirstOrDefault(c => c.Id == status.CategoryId);
                var categoryName = category is null ? "" : MessageProcessor.GetCategoryDisplayName(category, localizer);
                return localizer["Digest.BudgetLineCategory", categoryName, spentFormatted, limitFormatted].Value;
            }));

        return string.Join('\n', [
            localizer["Digest.RemindersHeader"], remindersSection,
            "",
            localizer["Digest.CalendarHeader"], calendarSection,
            "",
            localizer["Digest.ShoppingHeader"], shoppingSection,
            "",
            localizer["Digest.BudgetHeader"], budgetSection,
        ]);
    }
}
