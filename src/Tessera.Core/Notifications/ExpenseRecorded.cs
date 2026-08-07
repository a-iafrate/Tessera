namespace Tessera.Core.Notifications;

// CategoryId, not a display name — category names for system rows are resource keys and
// must resolve in the recipient's own culture (docs/09-localizzazione.md).
public sealed record ExpenseRecorded(
    Guid SpaceId,
    Guid ActorUserId,
    decimal Amount,
    string Currency,
    Guid? CategoryId,
    string? OriginChatId,
    DateTimeOffset At);
