using Tessera.Core.Spaces;

namespace Tessera.Ai.Llm;

// The variable tail of the prompt (docs/05-ottimizzazioni.md) — never the system prompt or
// the tool schema, or prompt caching breaks. Nothing here is user-generated content.
// RecentAction is set only within the short correction window (docs/10-conversazione.md) —
// its presence is also what tells LlmFallbackClient to include the correction tool at all.
//
// AccessByResource and HasLinkedCalendar aren't part of the prompt text itself — they drive
// which tools LlmTools.Build offers (docs/05-ottimizzazioni.md, "Schema dei tool per
// contesto"), which is why they travel on the same per-turn context rather than as separate
// parameters threaded through TryCompleteAsync.
public sealed record LlmContext(
    string Culture,
    string TimeZoneId,
    DateTimeOffset NowUtc,
    string SpaceName,
    IReadOnlyDictionary<ResourceKind, AccessLevel> AccessByResource,
    bool HasLinkedCalendar,
    string? RecentAction = null);
