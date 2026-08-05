using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingRemoveMatcher : IIntentMatcher
{
    public string Intent => "shopping.remove";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(rimuovi|togli|elimina)\s+(?<item>.+?)(\s+(dalla|nella)\s+lista.*)?$",
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
