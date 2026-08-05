using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// More specific than EnExpensesQueryMatcher — must be tried first by the router
// (registration order in Matchers.All).
public sealed class EnExpensesQueryByCategoryMatcher : IIntentMatcher
{
    public string Intent => "expenses.query.category";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*how\s+much\s+did\s+i\s+spend\s+(?:on|for)\s+(?<category>.+?)\s*[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new IntentMatch(Intent, 1.0, new Dictionary<string, string>
        {
            ["category"] = match.Groups["category"].Value,
        });
    }
}
