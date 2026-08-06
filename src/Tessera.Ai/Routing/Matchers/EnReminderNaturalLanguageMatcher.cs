using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// Recognizes the *intent* to set a reminder in natural language — not the date inside it
// (docs/05-ottimizzazioni.md: date parsing itself still needs L3).
public sealed class EnReminderNaturalLanguageMatcher : IIntentMatcher
{
    public string Intent => "reminders.natural";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*remind\s+me\s+(to|that)\s+.+$",
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
