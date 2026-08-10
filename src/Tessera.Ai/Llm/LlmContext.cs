namespace Tessera.Ai.Llm;

// The variable tail of the prompt (docs/05-ottimizzazioni.md) — never the system prompt or
// the tool schema, or prompt caching breaks. Nothing here is user-generated content.
// RecentAction is set only within the short correction window (docs/10-conversazione.md) —
// its presence is also what tells LlmFallbackClient to include the correction tool at all.
public sealed record LlmContext(
    string Culture, string TimeZoneId, DateTimeOffset NowUtc, string SpaceName, string? RecentAction = null);
