using System.Text.Json;

namespace Tessera.Core.Abstractions;

// One implementation (PayPalClient, Tessera.Integrations) — unlike ICalendarProvider there's a
// single payment provider by explicit product decision (docs/04-costi.md), not a polymorphic
// set. The interface exists anyway so Tessera.Data (PayPalSubscriptionService) depends only on
// Tessera.Core, the same layering ICalendarProvider gives calendar linking — Tessera.Data must
// never reference Tessera.Integrations directly (docs/01-architettura.md).
public interface IPaymentProvider
{
    // Lets PayPalSubscriptionService pick SubscriptionPlan.PayPalPlanIdSandbox vs
    // ...IdLive without needing its own copy of the PayPal:Environment config.
    bool IsLive { get; }

    Task<string> EnsureProductAsync(CancellationToken ct);

    Task<string> CreatePlanAsync(string productId, string planName, decimal monthlyPrice, string currency, CancellationToken ct);

    Task<(string SubscriptionId, string ApproveUrl)> CreateSubscriptionAsync(
        string providerPlanId, string returnUrl, string cancelUrl, string locale, CancellationToken ct);

    // Changes an existing subscription to a different plan. Returns an approve URL when PayPal
    // requires the payer to confirm the new terms (same "approve" HATEOAS link as
    // CreateSubscriptionAsync) — null means the change applied immediately, no redirect needed.
    // Which of the two happens isn't fully documented by PayPal and needs verifying against a
    // real sandbox run before this is trusted in production (docs/03-integrazioni.md).
    Task<string?> ReviseSubscriptionAsync(
        string providerSubscriptionId, string providerPlanId, string returnUrl, string cancelUrl, CancellationToken ct);

    // Cancels immediately — no confirmation step on PayPal's side, unlike Create/Revise. The
    // caller (PayPalSubscriptionService) doesn't wait for a webhook before reflecting this
    // locally, since there's no ambiguity left to resolve once the call succeeds.
    Task CancelSubscriptionAsync(string providerSubscriptionId, string reason, CancellationToken ct);

    Task<DateTimeOffset?> GetNextBillingTimeAsync(string providerSubscriptionId, CancellationToken ct);

    Task<bool> VerifyWebhookSignatureAsync(
        string transmissionId, string transmissionTime, string certUrl, string authAlgo, string transmissionSig,
        JsonElement webhookEvent, CancellationToken ct);
}
