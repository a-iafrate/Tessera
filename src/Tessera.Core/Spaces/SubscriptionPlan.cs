namespace Tessera.Core.Spaces;

// Value axes, not cost axes (docs/13-piano-miglioramenti.md, D1) — every field here is
// something a user can see the point of, unlike the LLM-call counter this plan used to sell
// directly. MaxCallsPerDay stays only as an internal economic guard (docs/04-costi.md); nothing
// here markets it. MaxLinkedBots stays too, but as an anti-abuse ceiling far above real family
// usage on every plan — sharing across Telegram/web is the product's whole growth loop and was
// never meant to be the thing Free paywalls.
public class SubscriptionPlan
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;

    // Anti-abuse only from here on (docs/13, D1) — not a marketed limit on any plan.
    public int MaxLinkedBots { get; set; }

    // Internal economic guard (docs/04-costi.md) — enforced in UsageService, never shown on
    // Pricing.razor.
    public int MaxCallsPerDay { get; set; }

    public decimal MonthlyPrice { get; set; }

    // Its own stored figure rather than a computed discount off MonthlyPrice — same placeholder
    // status as MonthlyPrice (docs/13-piano-miglioramenti.md, D2), kept adjustable independently
    // since the eventual discount is a business call, not a fixed formula.
    public decimal AnnualPrice { get; set; }

    public string Currency { get; set; } = "EUR";

    // Monthly-cycle PayPal billing plan ids.
    public string? PayPalPlanIdSandbox { get; set; }
    public string? PayPalPlanIdLive { get; set; }

    // Annual-cycle PayPal billing plan ids — a distinct PayPal resource per (plan, cycle,
    // environment): PayPal has no single plan that bills at two different frequencies.
    public string? PayPalPlanIdSandboxAnnual { get; set; }
    public string? PayPalPlanIdLiveAnnual { get; set; }

    // Replaces the old all-or-nothing AllowsReceiptScanning bool — a free space can still try
    // the thing the README calls out as the product's actual differentiator, just not without
    // limit (docs/13, D1: "un utente Free può scansionare qualche scontrino al mese").
    public int MaxReceiptsPerMonth { get; set; }

    // Per space, not per user — how many external calendars can be mapped into one space
    // (CalendarSpaceService.SetMappingAsync) before the plan's limit kicks in.
    public int MaxLinkedCalendars { get; set; }

    // How many months back ExpenseService.QueryHistoryAsync will search on this plan.
    // <= 0 means unlimited — deliberately not 0-means-zero-months, since "no history at all"
    // was never a state a plan is supposed to produce.
    public int HistoryMonths { get; set; }

    public bool AllowsExport { get; set; }

    // How many Spaces a single user may own at once (decision 2, docs/13: entitlement
    // propagates to *every* space the payer owns, which is what makes "own more than one"
    // possible to sell in the first place — the highest MaxSpacesOwned across a user's
    // currently-owned spaces' plans governs, per SpaceService.CanCreateAnotherSpaceAsync).
    public int MaxSpacesOwned { get; set; }
}
