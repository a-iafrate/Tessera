namespace Tessera.Core.Notifications;

// No text, only facts (docs/09-localizzazione.md) — rendering per recipient happens later,
// in the recipient's own culture.
public sealed record ShoppingItemAdded(
    Guid SpaceId,
    Guid ActorUserId,
    string ItemText,
    string? OriginChatId,
    DateTimeOffset At);
