namespace Tessera.Core.Spaces;

// One row per L3/LLM call, counted against SubscriptionPlan.MaxCallsPerDay
// (docs/04-costi.md) — deliberately minimal: no message text, no user id, nothing that would
// turn a cost-control counter into a second copy of conversation history.
public class UsageEvent
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
