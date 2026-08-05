using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnExpensesQueryMatcher : IIntentMatcher
{
    public string Intent => "expenses.query";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*how\s+much\s+did\s+i\s+spend\b",
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
