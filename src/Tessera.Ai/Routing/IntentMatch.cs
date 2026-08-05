namespace Tessera.Ai.Routing;

public record IntentMatch(string Intent, double Confidence, IReadOnlyDictionary<string, string> Slots);
