namespace Tessera.Core.Notifications;

// A single buffered occurrence inside an expense aggregation window (docs/13, C3). CategoryId,
// not a display name, for the same reason ExpenseRecorded itself carries one
// (docs/09-localizzazione.md) — category names are resource keys, resolved per recipient.
public sealed record ExpenseFact(
    Guid ActorUserId,
    string ActorDisplayName,
    decimal Amount,
    string Currency,
    Guid? CategoryId,
    string? OriginChatId) : IAggregatedNotificationFact;
