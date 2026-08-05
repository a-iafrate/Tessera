using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItExpensesQueryMatcher : IIntentMatcher
{
    public string Intent => "expenses.query";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*quanto\s+ho\s+speso\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new IntentMatch(Intent, 1.0, new Dictionary<string, string>());
    }
}
