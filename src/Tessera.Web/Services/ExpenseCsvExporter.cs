using System.Globalization;
using System.Text;
using Microsoft.Extensions.Localization;
using Tessera.Core.Expenses;
using Tessera.Core.Resources;

namespace Tessera.Web.Services;

// Culture-aware, not just a formatted dump (docs/13-piano-miglioramenti.md, E3;
// docs/09-localizzazione.md). Excel picks its CSV delimiter from the *system* locale's own list
// separator — for it-IT that's ';', not ',', because ',' is already the decimal separator
// there. Getting this wrong is exactly what "apre correttamente in Excel con cultura italiana"
// (the plan's own acceptance criterion) tests: a plain comma-CSV opens as one column per row
// under an Italian Excel install, not one column per field.
public static class ExpenseCsvExporter
{
    public static string Build(
        IReadOnlyList<Expense> expenses, IReadOnlyDictionary<Guid, Category> categoriesById,
        CultureInfo culture, IStringLocalizer<Messages> localizer)
    {
        var separator = culture.TextInfo.ListSeparator;
        var sb = new StringBuilder();

        // UTF-8 BOM — without it, Excel on Windows (the Italian case the acceptance criterion
        // names) guesses the wrong code page and mangles accented characters in Merchant/Note.
        sb.Append('﻿');

        AppendRow(sb, separator,
        [
            localizer["Expenses.Export.DateColumn"].Value,
            localizer["Expenses.Export.AmountColumn"].Value,
            localizer["Expenses.Export.CurrencyColumn"].Value,
            localizer["Expenses.Export.CategoryColumn"].Value,
            localizer["Expenses.Export.MerchantColumn"].Value,
            localizer["Expenses.Export.NoteColumn"].Value,
        ]);

        foreach (var expense in expenses)
        {
            var categoryName = expense.CategoryId is { } categoryId && categoriesById.TryGetValue(categoryId, out var category)
                ? MessageProcessor.GetCategoryDisplayName(category, localizer)
                : localizer["Expenses.NoCategory"].Value;

            // Long date, not the numeric "d" pattern the on-screen list uses — the numeric form
            // is exactly the day/month-order ambiguity docs/09-localizzazione.md warns about,
            // and a CSV cell has no screen-space excuse to prefer it.
            AppendRow(sb, separator,
            [
                expense.Date.ToString("d MMMM yyyy", culture),
                expense.Amount.ToString("N2", culture),
                expense.Currency,
                categoryName,
                expense.Merchant ?? "",
                expense.Note ?? "",
            ]);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string separator, IReadOnlyList<string> fields)
    {
        sb.AppendJoin(separator, fields.Select(f => QuoteIfNeeded(f, separator)));
        sb.Append("\r\n");
    }

    // RFC 4180 escaping: quote a field that contains the delimiter, a quote, or a newline, and
    // double up any quotes inside it. Merchant/Note are free text (docs/09-localizzazione.md:
    // user content, never sanitized away) and can contain any of these.
    private static string QuoteIfNeeded(string field, string separator) =>
        field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
}
