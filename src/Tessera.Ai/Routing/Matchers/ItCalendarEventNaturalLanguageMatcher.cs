using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// Must run before ItShoppingAddMatcher (see Matchers.All ordering): "aggiungi"/"metti"/"segna"
// are also how you add to the shopping list, so without this "aggiungi la macchina domani alle
// 17 in calendario" is indistinguishable from a shopping item unless something intercepts it
// first. Only the *intent* is recognized here — date parsing itself still needs L3, same
// reasoning as ItReminderNaturalLanguageMatcher (docs/05-ottimizzazioni.md).
public sealed class ItCalendarEventNaturalLanguageMatcher : IIntentMatcher
{
    public string Intent => "calendar.natural";

    public string Culture => "it";

    private static readonly Regex Pattern = new(
        @"^\s*(aggiungi|metti|segna|crea|programma)\b.*\b(calendario|evento|riunione|appuntamento)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text) =>
        Pattern.IsMatch(text) ? new IntentMatch(Intent, 1.0, new Dictionary<string, string>()) : null;
}
