using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// High priority (docs/10-conversazione.md, docs/05-ottimizzazioni.md): registered first so it
// wins over any other L2 match on the same short phrase.
public sealed class EnUndoMatcher : IIntentMatcher
{
    public string Intent => "undo";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*(undo|cancel that|(wait,?\s*)?no,?\s*wait|no,?\s*undo that)\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text) =>
        Pattern.IsMatch(text) ? new IntentMatch(Intent, 1.0, new Dictionary<string, string>()) : null;
}
