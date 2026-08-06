using System.Globalization;
using System.Text.RegularExpressions;

namespace Tessera.Ai.Commands;

// The trivial, deterministic forms of /remind (docs/05-ottimizzazioni.md: "/reminder
// 15/09 ..." is explicitly the example of a form that does NOT need the LLM). Anything
// else — natural language dates — returns null and falls through to L3, not handled here:
// "non vale scrivere quelle regex a mano" for dates.
public static class RemindCommandParser
{
    // The time group is optional, but if present it is matched as a unit: an out-of-range
    // hour/minute must be rejected outright, not silently reinterpreted as reminder text.
    private static readonly Regex Once = new(
        @"^(?<day>\d{1,2})/(?<month>\d{1,2})(?:/(?<year>\d{4}))?(?:\s+(?<hour>\d{1,2}):(?<minute>\d{2}))?\s+(?<text>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Recurring = new(
        $@"^(?<freq>{FrequencyKeywords.Pattern})\s+(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RemindCommand? Parse(string argsText)
    {
        var trimmed = argsText.Trim();
        if (trimmed.Length == 0)
        {
            return new RemindCommand.ListPending();
        }

        var recurringMatch = Recurring.Match(trimmed);
        if (recurringMatch.Success)
        {
            var frequency = FrequencyKeywords.Parse(recurringMatch.Groups["freq"].Value);
            return new RemindCommand.CreateRecurring(frequency, recurringMatch.Groups["text"].Value.Trim());
        }

        var onceMatch = Once.Match(trimmed);
        if (!onceMatch.Success || !TryParseDate(onceMatch, out var date))
        {
            return null;
        }

        if (!onceMatch.Groups["hour"].Success)
        {
            return new RemindCommand.CreateOnce(date, null, onceMatch.Groups["text"].Value.Trim());
        }

        if (!TryParseTime(onceMatch, out var time))
        {
            // A time-shaped group was present but out of range — reject rather than let
            // it fall through and end up embedded in the reminder text.
            return null;
        }

        return new RemindCommand.CreateOnce(date, time, onceMatch.Groups["text"].Value.Trim());
    }

    private static bool TryParseDate(Match match, out DateOnly date)
    {
        date = default;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var year = match.Groups["year"].Success
            ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture)
            : DateTime.UtcNow.Year;

        if (day is < 1 or > 31 || month is < 1 or > 12)
        {
            return false;
        }

        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseTime(Match match, out TimeOnly time)
    {
        time = default;
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }
}
