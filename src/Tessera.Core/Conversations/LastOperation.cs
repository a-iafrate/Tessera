namespace Tessera.Core.Conversations;

// One reversible operation per user, not a stack (docs/10-conversazione.md): a multi-level
// undo in chat is confusing ("undo three times — what did I just undo?") and per-user rather
// than per-space, so your partner's edit never gets undone by your "undo".
public class LastOperation
{
    public Guid UserId { get; set; }
    public Guid SpaceId { get; set; }
    public string OperationType { get; set; } = null!;
    public string UndoPayloadJson { get; set; } = null!;
    public DateTimeOffset PerformedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsUndone { get; set; }
}
