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
        TimeZoneInfo timeZone, CultureInfo culture, IStringLocalizer<Messages> localizer) =>
        Format(BuildSections(daily, categories, currency, timeZone, culture, localizer), localizer);

    // Joins BuildSections' output into the single plain-text rendering every channel except
    // email uses as-is. Exposed separately so DailyDigestJob can build the sections once and
    // still get email's own HTML rendering from the same data (docs/13-piano-miglioramenti.md, C1).
    public static string Format(IReadOnlyList<(string Header, string Body)> sections, IStringLocalizer<Messages> localizer) =>
        sections.Count == 0
            ? localizer["Digest.AllEmpty"]
            : string.Join("\n\n", sections.Select(s => $"{s.Header}\n{s.Body}"));

    public static IReadOnlyList<(string Header, string Body)> BuildSections(
        DailyDigest daily, IReadOnlyList<Category> categories, string currency,
        TimeZoneInfo timeZone, CultureInfo culture, IStringLocalizer<Messages> localizer)
    {
        // Empty sections are dropped entirely, header included — unlike the web dashboard
        // (docs/12-stile-sito.md), a proactive daily push has no "add" affordance to preserve
        // by keeping the section visible, so an empty one is pure noise, not a nudge to act.
        var sections = new List<(string Header, string Body)>();

        if (daily.RemindersToday.Count > 0)
        {
            var body = string.Join('\n', daily.RemindersToday.Select(r =>
                localizer["Reminders.ListItemLine", MessageProcessor.FormatDueAt(r.DueAt, timeZone, culture), r.Text].Value));
            sections.Add((localizer["Digest.RemindersHeader"].Value, body));
        }

        if (daily.EventsToday.Count > 0)
        {
            var body = string.Join('\n', daily.EventsToday.Select(e =>
                localizer["Calendars.EventLine", MessageProcessor.FormatDueAt(e.Start, timeZone, culture), e.Title].Value));
            sections.Add((localizer["Digest.CalendarHeader"].Value, body));
        }

        if (daily.MissingItems.Count > 0)
        {
            var body = string.Join('\n', daily.MissingItems.Select(i => localizer["Shopping.ListItemLine", i.RawText].Value));
            sections.Add((localizer["Digest.ShoppingHeader"].Value, body));
        }

        if (daily.BudgetStatuses.Count > 0)
        {
            var body = string.Join('\n', daily.BudgetStatuses.Select(status =>
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
            sections.Add((localizer["Digest.BudgetHeader"].Value, body));
        }

        return sections;
    }
}
