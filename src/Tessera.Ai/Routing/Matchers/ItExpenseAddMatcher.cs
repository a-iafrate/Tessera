using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItExpenseAddMatcher : IIntentMatcher
{
    public string Intent => "expenses.add";

    public string Culture => "it";

    // Amount kept as raw text — parsing with the user's culture happens downstream
    // (docs/09-localizzazione.md), never here. "per/in" names an explicit category;
    // "da" names a merchant, resolved via the learned mapping (docs/02-modello-dati.md) —
    // the two are mutually exclusive in a single expense.
    private static readonly Regex Pattern = new(
        @"^\s*(?:ho\s+speso|spesa\s+di|registra(?:\s+una\s+spesa\s+di)?)\s+(?<amount>\d+(?:[.,]\d+)?)\s*(?:euro|€)?(?:\s+(?:per|in)\s+(?<category>.+?)|\s+da\s+(?<merchant>.+?))?\s*[?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var slots = new Dictionary<string, string> { ["amount"] = match.Groups["amount"].Value };
        if (match.Groups["category"].Success)
        {
            slots["category"] = match.Groups["category"].Value;
        }
        if (match.Groups["merchant"].Success)
        {
            slots["merchant"] = match.Groups["merchant"].Value;
        }

        return new IntentMatch(Intent, 1.0, slots);
    }
}
