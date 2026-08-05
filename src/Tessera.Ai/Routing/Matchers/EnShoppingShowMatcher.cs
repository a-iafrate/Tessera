using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class EnShoppingShowMatcher : IIntentMatcher
{
    public string Intent => "shopping.show";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*(what'?s\s+on\s+the\s+list|show(\s+me)?\s+the\s+list)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text)
    {
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new IntentMatch(Intent, 1.0, new Dictionary<string, string>());
    }
}
