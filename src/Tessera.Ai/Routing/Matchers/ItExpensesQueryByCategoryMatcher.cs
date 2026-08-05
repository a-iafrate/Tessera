using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// More specific than ItExpensesQueryMatcher — must be tried first by the router
// (registration order in Matchers.All) or "quanto ho speso in benzina" would match the
// generic monthly-total matcher instead, since that one has no trailing anchor.
public sealed class ItExpensesQueryByCategoryMatcher : IIntentMatcher
{
    public string Intent => "expenses.query.category";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*quanto\s+ho\s+speso\s+(?:in|per)\s+(?<category>.+?)\s*[?!.]*$",
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
