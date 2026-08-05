namespace Tessera.Ai.Routing;

// L2 of the router (docs/05-ottimizzazioni.md). Below the confidence threshold, or for a
// culture with no matcher at all, the caller falls back to L3 (LLM) — this class never
// guesses: it returns null rather than a low-confidence match.
public sealed class IntentRouter
{
    public const double ConfidenceThreshold = 0.85;

    private readonly Dictionary<string, IIntentMatcher[]> byCulture;

    public IntentRouter(IEnumerable<IIntentMatcher> matchers)
    {
        byCulture = matchers
            .GroupBy(m => m.Culture)
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    public IntentMatch? TryRoute(string text, string culture)
    {
        var lang = culture.Split('-')[0];
        if (!byCulture.TryGetValue(lang, out var matchers))
        {
            return null;
        }

        foreach (var matcher in matchers)
        {
            var match = matcher.TryMatch(text);
            if (match is not null && match.Confidence >= ConfidenceThreshold)
            {
                return match;
            }
        }

        return null;
    }
}
