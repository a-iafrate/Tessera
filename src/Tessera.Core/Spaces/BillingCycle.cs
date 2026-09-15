namespace Tessera.Core.Spaces;

// How often a SpaceSubscription bills — one PayPal billing plan per (SubscriptionPlan, cycle,
// environment) pair, since PayPal has no notion of "the same plan at a different frequency"
// (docs/13-piano-miglioramenti.md, D2). Stored on SpaceSubscription so a revision that only
// changes plan tier can keep the payer's chosen cycle without asking again.
public enum BillingCycle
{
    Monthly = 0,
    Annual = 1,
}
