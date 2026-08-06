using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// Recognizes the *intent* to set a reminder in natural language — not the date inside it.
// Without this, "ricordami di comprare il latte domani" falls into the generic
// "I didn't get that", which is worse than pointing at the /remind syntax that actually
// works today (docs/05-ottimizzazioni.md: date parsing itself still needs L3).
public sealed class ItReminderNaturalLanguageMatcher : IIntentMatcher
{
    public string Intent => "reminders.natural";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(ricordami\s+(di|che)|ricordati\s+che)\s+.+$",
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
