namespace Tessera.Core.Conversations;

// Volatile — "which of the two meetings did you mean?", expired aggressively. Not the
// bot's long-term memory (that's the domain database). One row per user, upserted
// (docs/02-modello-dati.md).
public class ConversationState
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? ActiveSpaceId { get; set; }
    public string? PendingIntent { get; set; }
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // A serialized IReadOnlyList<RecentExchange> (docs/05-ottimizzazioni.md, "Storico
    // limitato") — deliberately its own column, not folded into StateJson/PendingIntent above:
    // those two are a single-slot "one pending confirmation at a time" pair (reminder date
    // confirm, calendar event confirm, space choice, ...), and this needs to keep accumulating
    // across turns independently of whatever confirmation flow, if any, is also in progress on
    // this same row. Each entry carries its own timestamp and is TTL-filtered by
    // RecentExchange.Parse, not by the row's shared ExpiresAt above.
    public string? RecentExchangesJson { get; set; }
}
