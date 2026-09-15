namespace Tessera.Core.Spaces;

public enum UsageEventKind
{
    L3Call,
    ReceiptScan,
}

// One row per L3/LLM call or receipt scan, counted against SubscriptionPlan.MaxCallsPerDay /
// MaxReceiptsPerMonth respectively (docs/04-costi.md, docs/13-piano-miglioramenti.md D1) —
// deliberately minimal: no message text, no user id, nothing that would turn a cost-control
// counter into a second copy of conversation history.
public class UsageEvent
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public UsageEventKind Kind { get; set; } = UsageEventKind.L3Call;
}
