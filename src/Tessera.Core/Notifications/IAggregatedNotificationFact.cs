namespace Tessera.Core.Notifications;

// Lets NotificationAggregationFlushJob tell same-actor bursts (still worth naming the actor in
// the aggregate message) from multi-actor ones (rendered without a subject) without a runtime
// type switch per fact type (docs/13, C3).
public interface IAggregatedNotificationFact
{
    Guid ActorUserId { get; }
}
