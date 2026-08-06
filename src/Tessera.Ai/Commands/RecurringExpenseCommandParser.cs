using System.Text.RegularExpressions;

namespace Tessera.Ai.Commands;

// The trivial, deterministic form of /recurring. Amount is always present in the schema
// (docs/02-modello-dati.md) even for reminder-only rules — it's a best-known estimate for
// variable-amount bills, not the auto-registered figure.
public static class RecurringExpenseCommandParser
{
    private static readonly Regex CreatePattern = new(
        $@"^(?<freq>{FrequencyKeywords.Pattern})\s+(?:(?<reminderOnly>reminder|promemoria)\s+)?(?<amount>\d+(?:[.,]\d+)?)\s+(?<description>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RecurringExpenseCommand? Parse(string argsText)
    {
        var trimmed = argsText.Trim();
        if (trimmed.Length == 0)
        {
            return new RecurringExpenseCommand.ListActive();
        }

        var match = CreatePattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var frequency = FrequencyKeywords.Parse(match.Groups["freq"].Value);
        var autoRegister = !match.Groups["reminderOnly"].Success;
        return new RecurringExpenseCommand.Create(
            frequency, autoRegister, match.Groups["amount"].Value, match.Groups["description"].Value.Trim());
    }
}
