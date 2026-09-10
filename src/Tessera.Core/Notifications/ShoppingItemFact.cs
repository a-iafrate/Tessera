namespace Tessera.Core.Notifications;

// A single buffered occurrence inside a shopping-item aggregation window (docs/13, C3). No
// rendered text, only facts — same reason the domain events themselves carry none
// (docs/09-localizzazione.md): rendering happens once, per recipient, at flush time.
public sealed record ShoppingItemFact(Guid ActorUserId, string ActorDisplayName, string ItemText, string? OriginChatId)
    : IAggregatedNotificationFact;
