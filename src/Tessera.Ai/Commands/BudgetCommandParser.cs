using System.Text.RegularExpressions;

namespace Tessera.Ai.Commands;

// The trivial, deterministic form of /budget. A bare amount sets the space's overall
// monthly limit; a leading word (or phrase) resolved against categories elsewhere sets a
// per-category limit (docs/02-modello-dati.md: CategoryId null = budget complessivo).
public static class BudgetCommandParser
{
    private static readonly Regex SetPattern = new(
        @"^(?:(?<category>.+?)\s+)?(?<amount>\d+(?:[.,]\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static BudgetCommand? Parse(string argsText)
    {
        var trimmed = argsText.Trim();
        if (trimmed.Length == 0)
        {
            return new BudgetCommand.ListActive();
        }

        var match = SetPattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var amountText = match.Groups["amount"].Value;
        return match.Groups["category"].Success
            ? new BudgetCommand.SetCategory(match.Groups["category"].Value.Trim(), amountText)
            : new BudgetCommand.SetOverall(amountText);
    }
}
