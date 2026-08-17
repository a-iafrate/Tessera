using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingRemoveMatcher : IIntentMatcher
{
    public string Intent => "shopping.remove";

    public string Culture => "it";

    // The (mia|tua|nostra)? before "lista" matters: without it, "rimuovi il latte dalla mia
    // lista" fails to strip the suffix and the whole tail gets swallowed into item.
    private static readonly Regex Pattern = new(
        @"^\s*(rimuovi|togli|elimina)\s+(?<item>.+?)(\s+(dalla|nella)\s+(mia\s+|tua\s+|nostra\s+)?lista.*)?[?!.]*$",
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
