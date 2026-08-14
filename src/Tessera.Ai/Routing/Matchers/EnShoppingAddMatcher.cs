using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnShoppingAddMatcher : IIntentMatcher
{
    public string Intent => "shopping.add";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*(add|put)\s+(?<item>.+?)(\s+on\s+the\s+list.*)?[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "add note X" means create a note, not add "note X" to the shopping list — notes have
    // no L2 matcher of their own (deliberately: docs/06-roadmap.md, only L1/L3), so without
    // this exclusion this pattern swallows it before L3 ever gets a chance
    // (docs/05-ottimizzazioni.md: the fast path must never be the only path).
    private static readonly Regex LooksLikeNote = new(
        @"^(a\s+|some\s+)?notes?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
