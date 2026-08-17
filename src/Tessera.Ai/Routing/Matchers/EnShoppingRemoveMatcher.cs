using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnShoppingRemoveMatcher : IIntentMatcher
{
    public string Intent => "shopping.remove";

    public string Culture => "en";

    // (the|my|your|our) matters: without it, "remove the milk from my list" fails to strip
    // the suffix (no literal match for "from the list") and the whole tail — "the milk from
    // my list" — gets swallowed into item.
    private static readonly Regex Pattern = new(
        @"^\s*(remove|delete)\s+(?<item>.+?)(\s+(from|on)\s+(the|my|your|our)\s+list.*)?[?!.]*$",
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
