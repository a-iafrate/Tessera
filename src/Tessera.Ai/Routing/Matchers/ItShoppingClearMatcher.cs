using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

public sealed class ItShoppingClearMatcher : IIntentMatcher
{
    public string Intent => "shopping.clear";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(svuota|vuota|cancella)\s+la\s+lista\s*$",
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
