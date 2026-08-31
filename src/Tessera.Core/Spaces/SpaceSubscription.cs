namespace Tessera.Core.Spaces;

// One row per Space that has ever started a PayPal subscription (docs/02-modello-dati.md,
// docs/03-integrazioni.md) — separate from Space itself, same principle as LinkedAccount for
// calendars: the state here changes from external webhook events, not from anything the user
// does in the console directly.
public class SpaceSubscription
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }

    // PayPal's subscription id ("I-XXXXXXXXXXXX"), assigned when CreateSubscriptionAsync calls
    // POST /v1/billing/subscriptions.
    public string PayPalSubscriptionId { get; set; } = null!;
    public Guid PlanId { get; set; }

    // Mirrors PayPal's own vocabulary (APPROVAL_PENDING, ACTIVE, SUSPENDED, CANCELLED, EXPIRED)
    // rather than a local enum — one less mapping to keep in sync as PayPal's webhook events
    // arrive, and the raw value is exactly what shows up in support conversations with PayPal.
    public string Status { get; set; } = null!;
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
