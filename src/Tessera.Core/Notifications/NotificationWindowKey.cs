namespace Tessera.Core.Notifications;

// Identifies one 60-second-or-longer aggregation window (docs/13-piano-miglioramenti.md, C3).
// EventType is the domain event's own type name (nameof(ShoppingItemAdded), etc.) — an
// internal grouping key, never rendered to a user.
public readonly record struct NotificationWindowKey(Guid SpaceId, Guid RecipientUserId, string EventType);
