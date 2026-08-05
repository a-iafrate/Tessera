using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingAddMatcher : IIntentMatcher
{
    public string Intent => "shopping.add";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(aggiungi|metti|segna|aggiungimi)\s+(?<item>.+?)(\s+(alla|in|nella)\s+lista.*)?[?!.]*$",
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
