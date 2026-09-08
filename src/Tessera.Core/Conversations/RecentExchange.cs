using System.Text.Json;

namespace Tessera.Core.Conversations;

// A structured breadcrumb of one L3 turn — the user's own text plus what the model actually did
// with it, never the assistant's prose reply (docs/05-ottimizzazioni.md: "uno stato
// conversazionale strutturato ... invece del testo integrale"). Lets a follow-up like "and in
// February?" get answered without the user re-stating the whole question: the model sees that
// the previous turn called query_expense_history with a January date range and can construct
// the analogous call itself. ToolName/ToolArgsJson are both null for a turn that got a plain-
// text reply instead of calling a tool — still worth keeping, since "no, I meant X" needs to
// know a turn happened at all, not what it did.
//
// Lives in Tessera.Core, not Tessera.Web, so the TTL/trim logic is unit-testable without a
// database (docs' own convention for anything that isn't itself a DB query).
public sealed record RecentExchange(string UserText, string? ToolName, string? ToolArgsJson, DateTimeOffset AtUtc)
{
    // Same 30-minute volatility as the rest of ConversationState (docs/02-modello-dati.md), but
    // enforced per-entry against each entry's own AtUtc rather than the row's shared
    // ExpiresAt — that column belongs to the unrelated pending-confirmation slot on the same
    // row (docs/13-piano-miglioramenti.md, A3).
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private const int MaxEntries = 4;

    // Filters out anything past its TTL at read time, so a stale entry never reaches the LLM
    // context and — since Append only ever works from this method's output — never survives
    // into the next write either. No separate cleanup job needed for this column.
    public static IReadOnlyList<RecentExchange> Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        List<RecentExchange>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<RecentExchange>>(json);
        }
        catch (JsonException)
        {
            // Malformed or from an incompatible earlier shape — treat as no history rather
            // than fail the whole L3 turn over a breadcrumb.
            return [];
        }

        return entries?.Where(e => now - e.AtUtc <= Ttl).ToList() ?? [];
    }

    // existing must already be Parse's output (TTL-filtered) — this only adds the newest turn
    // and caps the tail, it doesn't re-check age.
    public static string Serialize(IReadOnlyList<RecentExchange> existing, RecentExchange newEntry) =>
        JsonSerializer.Serialize(existing.Append(newEntry).TakeLast(MaxEntries).ToList());
}
