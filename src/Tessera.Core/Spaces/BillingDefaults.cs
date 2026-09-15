namespace Tessera.Core.Spaces;

// Cross-cutting subscription constants that aren't per-plan data (docs/13-piano-miglioramenti.md,
// D2) — a single source of truth so the PayPal billing-plan definition (PayPalClient) and the
// pricing copy (Pricing.razor, SpaceUsage.razor) can't drift apart.
public static class BillingDefaults
{
    // Applied as a TRIAL billing cycle ahead of the REGULAR one on every paid PayPal plan
    // (PayPalClient.CreatePlanAsync) — a placeholder like SubscriptionPlan.MonthlyPrice, tunable
    // without a schema change since it's not stored per plan.
    public const int TrialDays = 14;
}
