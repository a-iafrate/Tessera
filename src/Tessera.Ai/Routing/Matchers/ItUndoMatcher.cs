using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// High priority (docs/10-conversazione.md, docs/05-ottimizzazioni.md): registered first so it
// wins over any other L2 match on the same short phrase.
public sealed class ItUndoMatcher : IIntentMatcher
{
    public string Intent => "undo";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(annulla( operazione| tutto)?|(no,?\s*)?aspetta,?\s*no|no,?\s*aspetta)\s*[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text) =>
        Pattern.IsMatch(text) ? new IntentMatch(Intent, 1.0, new Dictionary<string, string>()) : null;
}
