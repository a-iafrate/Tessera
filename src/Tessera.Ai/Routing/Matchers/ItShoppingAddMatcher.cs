using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingAddMatcher : IIntentMatcher
{
    public string Intent => "shopping.add";

    public string Culture => "it";

    // The (mia|tua|nostra)? before "lista" matters: without it, "aggiungi il latte alla mia
    // lista" fails to strip the suffix (no literal match for "alla ... lista" with "mia" in
    // between) and the whole tail — "il latte alla mia lista" — gets swallowed into item.
    private static readonly Regex Pattern = new(
        @"^\s*(aggiungi|metti|segna|aggiungimi)\s+(?<item>.+?)(\s+(alla|in|nella)\s+(mia\s+|tua\s+|nostra\s+)?lista.*)?[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "aggiungi nota X" means create a note, not add "nota X" to the shopping list — notes
    // have no L2 matcher of their own (deliberately: docs/06-roadmap.md, only L1/L3), so
    // without this exclusion this pattern swallows it before L3 ever gets a chance
    // (docs/05-ottimizzazioni.md: the fast path must never be the only path).
    private static readonly Regex LooksLikeNote = new(
        @"^(una\s+|la\s+|le\s+|delle\s+)?(nota|note)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var item = match.Groups["item"].Value;
        if (LooksLikeNote.IsMatch(item))
        {
            return null;
        }

        return new IntentMatch(Intent, 1.0, new Dictionary<string, string>
        {
            ["item"] = item,
        });
    }
}
