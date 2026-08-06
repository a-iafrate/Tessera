using System.Text.RegularExpressions;

namespace Tessera.Ai.Commands;

public static class ExpenseCommandParser
{
    private static readonly Regex Pattern = new(
        @"^(?<amount>\d+(?:[.,]\d+)?)(?:\s+(?<category>.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ExpenseCommand? Parse(string argsText)
    {
        var trimmed = argsText.Trim();
        var match = Pattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var categoryGroup = match.Groups["category"];
        return new ExpenseCommand(match.Groups["amount"].Value, categoryGroup.Success ? categoryGroup.Value.Trim() : null);
    }
}
