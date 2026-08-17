using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingCheckMatcher : IIntentMatcher
{
    public string Intent => "shopping.check";

    public string Culture => "it";

    // The (mia|tua|nostra)? before "lista" matters: without it, "spunta il latte dalla mia
    // lista" fails to strip the suffix and the whole tail gets swallowed into item.
    private static readonly Regex Pattern = new(
        @"^\s*spunta\s+(?<item>.+?)(\s+(dalla|nella)\s+(mia\s+|tua\s+|nostra\s+)?lista.*)?[?!.]*$",
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
            ["item"] = match.Groups["item"].Value,
        });
    }
}
