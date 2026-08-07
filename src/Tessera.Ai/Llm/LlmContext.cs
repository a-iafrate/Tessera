namespace Tessera.Ai.Llm;

// The variable tail of the prompt (docs/05-ottimizzazioni.md) — never the system prompt or
// the tool schema, or prompt caching breaks. Nothing here is user-generated content.
public sealed record LlmContext(string Culture, string TimeZoneId, DateTimeOffset NowUtc, string SpaceName);
