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

    // PayPal's billing plan id (v1/billing/plans, "P-XXXX") for this tier — separate columns
    // per PayPal environment because the database is shared between test and production
    // (docs/03-integrazioni.md): sandbox and live are different PayPal accounts with
    // unrelated ids, and a single column would make going live silently reuse a sandbox plan
    // id that doesn't exist there. Null until PayPalSubscriptionService.EnsurePlansProvisionedAsync
    // creates the corresponding one, and always null for Free, which has no PayPal plan.
    public string? PayPalPlanIdSandbox { get; set; }
    public string? PayPalPlanIdLive { get; set; }

    // Scontrini via vision (docs/06-roadmap.md Fase 4, docs/04-costi.md: ~€0,002-0,005 a
    // scontrino) — a real per-scan cost on top of the daily call allowance, so it's its own
    // flag rather than folded into MaxCallsPerDay. False only on Free.
    public bool AllowsReceiptScanning { get; set; }
}
