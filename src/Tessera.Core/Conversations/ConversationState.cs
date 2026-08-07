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
}
