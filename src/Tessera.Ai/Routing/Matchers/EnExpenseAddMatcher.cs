using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnExpenseAddMatcher : IIntentMatcher
{
    public string Intent => "expenses.add";

    public string Culture => "en";

    // "for/on" names an explicit category; "at" names a merchant, resolved via the
    // learned mapping (docs/02-modello-dati.md) — mutually exclusive in a single expense.
    private static readonly Regex Pattern = new(
        @"^\s*(?:spent|record(?:\s+an\s+expense\s+of)?|log)\s+(?<amount>\d+(?:[.,]\d+)?)\s*(?:euros?|€|dollars?|\$)?(?:\s+(?:for|on)\s+(?<category>.+?)|\s+at\s+(?<merchant>.+?))?\s*[?!.]*$",
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
