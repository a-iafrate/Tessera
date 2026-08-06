namespace Tessera.Core.Spaces;

// One row per commercial tier (docs/04-costi.md), shared across every Space on that tier —
// not a per-Space copy. A Space references one via SubscriptionPlanId.
public class SubscriptionPlan
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public int MaxLinkedBots { get; set; }
    public int MaxCallsPerDay { get; set; }
    public decimal MonthlyPrice { get; set; }
    public string Currency { get; set; } = "EUR";
}
