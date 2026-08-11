using System.Text.RegularExpressions;

namespace Tessera.Ai.Routing.Matchers;

// Must run before EnShoppingAddMatcher (see Matchers.All ordering) — same collision as its
// Italian counterpart: "add"/"put" are also how you add to the shopping list.
public sealed class EnCalendarEventNaturalLanguageMatcher : IIntentMatcher
{
    public string Intent => "calendar.natural";

    public string Culture => "en";

    private static readonly Regex Pattern = new(
        @"^\s*(add|put|schedule|create)\b.*\b(calendar|event|meeting|appointment)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IntentMatch? TryMatch(string text) =>
        Pattern.IsMatch(text) ? new IntentMatch(Intent, 1.0, new Dictionary<string, string>()) : null;
}
