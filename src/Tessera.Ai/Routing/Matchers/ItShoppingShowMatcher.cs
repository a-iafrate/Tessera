using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingShowMatcher : IIntentMatcher
{
    public string Intent => "shopping.show";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(cosa\s+c'è\s+(in|nella)\s+lista|mostra(mi)?\s+la\s+lista|fammi\s+vedere\s+la\s+lista)\s*[?!.]*$",
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
