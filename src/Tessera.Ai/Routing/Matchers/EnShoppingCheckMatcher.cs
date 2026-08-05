using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnShoppingCheckMatcher : IIntentMatcher
{
    public string Intent => "shopping.check";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*check(\s+off)?\s+(?<item>.+?)(\s+(from|on)\s+the\s+list.*)?$",
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
