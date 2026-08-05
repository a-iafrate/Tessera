namespace Tessera.Ai.Routing;

public interface IIntentMatcher
{
    string Intent { get; }

    // "it", "en" — regex are per-language, matchers exist only for cultures whose
    // corpus can actually be evaluated (docs/09-localizzazione.md).
    string Culture { get; }

    IntentMatch? TryMatch(string text);
}
