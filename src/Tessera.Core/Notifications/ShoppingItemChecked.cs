namespace Tessera.Core.Notifications;

public sealed record ShoppingItemChecked(
    Guid SpaceId,
    Guid ActorUserId,
    string ItemText,
    string? OriginChatId,
    DateTimeOffset At);
