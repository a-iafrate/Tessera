using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// Orchestrates IPaymentProvider (the raw API wrapper, PayPalClient in Tessera.Integrations)
// against SpaceSubscription/SubscriptionPlan/Space (docs/02-modello-dati.md,
// docs/03-integrazioni.md). The provider knows nothing about Tessera's domain — this is where
// "an ACTIVATED event for subscription I-XXXX" becomes "Space YYYY is now on the Plus plan".
public sealed class PayPalSubscriptionService(TesseraDbContext db, IPaymentProvider paymentProvider, ILogger<PayPalSubscriptionService> logger)
{
    // Idempotent — safe to call on every startup or repeatedly by hand. Only ever touches paid
    // plans (Free has no PayPal plan, docs/02-modello-dati.md) and only plans that don't
    // already have a PayPalPlanId, so re-running after the first successful provisioning is a
    // no-op network-wise beyond the initial product lookup.
    public async Task EnsurePlansProvisionedAsync(CancellationToken ct)
    {
        var isLive = paymentProvider.IsLive;
        var plans = isLive
            ? await db.SubscriptionPlans.Where(x => x.Id != SystemPlanIds.Free && x.PayPalPlanIdLive == null).ToListAsync(ct)
            : await db.SubscriptionPlans.Where(x => x.Id != SystemPlanIds.Free && x.PayPalPlanIdSandbox == null).ToListAsync(ct);
        if (plans.Count == 0)
        {
            return;
        }

        var productId = await paymentProvider.EnsureProductAsync(ct);
        foreach (var plan in plans)
        {
            var payPalPlanId = await paymentProvider.CreatePlanAsync(productId, plan.Name, plan.MonthlyPrice, plan.Currency, ct);
            if (isLive)
            {
                plan.PayPalPlanIdLive = payPalPlanId;
            }
            else
            {
                plan.PayPalPlanIdSandbox = payPalPlanId;
            }

            logger.LogInformation("Created PayPal ({Environment}) billing plan {PayPalPlanId} for {PlanName}",
                isLive ? "live" : "sandbox", payPalPlanId, plan.Name);
        }

        await db.SaveChangesAsync(ct);
    }

    // A Space with one of these already has money moving on PayPal's side — ACTIVE obviously,
    // APPROVAL_PENDING because the user may still complete the approval and end up with two
    // live subscriptions if a second one is started in the meantime. SUSPENDED/CANCELLED/EXPIRED
    // don't block: nothing is being charged, so starting a fresh subscription is safe.
    private static readonly string[] BlockingStatuses = ["ACTIVE", "APPROVAL_PENDING"];

    // Starts a subscription and returns the PayPal URL the browser must be redirected to for
    // approval — the subscription only becomes real once the webhook confirms ACTIVATED
    // (docs/03-integrazioni.md), so the row created here starts in APPROVAL_PENDING and
    // Space.PlanId is deliberately left untouched until then.
    public async Task<string> CreateSubscriptionAsync(Guid spaceId, Guid planId, string returnUrl, string cancelUrl, string locale, CancellationToken ct)
    {
        // Checked here too, not just in the UI (SpaceUsage.razor) — without this, clicking
        // Subscribe twice (two tabs, a slow first redirect) would leave PayPal charging the
        // same Space for two plans at once, with nothing in Tessera itself to notice.
        //
        // Only the most recent row matters — a Space accumulates history (subscribe, cancel,
        // subscribe again), and an old superseded row can be sitting in APPROVAL_PENDING
        // forever (e.g. its webhook was never delivered, docs/03-integrazioni.md's ngrok
        // caveat) without that meaning anything about the space's *current* state. Matches the
        // ordering GetForSpaceAsync/ReviseSubscriptionAsync/CancelSubscriptionAsync already use.
        var latest = await db.SpaceSubscriptions
            .Where(x => x.SpaceId == spaceId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is not null && BlockingStatuses.Contains(latest.Status))
        {
            throw new InvalidOperationException(
                $"Space {spaceId} already has a {latest.Status} PayPal subscription — cancel it before starting another.");
        }

        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == planId, ct);
        var payPalPlanId = paymentProvider.IsLive ? plan.PayPalPlanIdLive : plan.PayPalPlanIdSandbox;
        if (payPalPlanId is null)
        {
            throw new InvalidOperationException(
                $"Plan {plan.Name} has no PayPal plan id for the {(paymentProvider.IsLive ? "live" : "sandbox")} environment — run EnsurePlansProvisionedAsync first.");
        }

        var (subscriptionId, approveUrl) = await paymentProvider.CreateSubscriptionAsync(payPalPlanId, returnUrl, cancelUrl, locale, ct);

        db.SpaceSubscriptions.Add(new SpaceSubscription
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            PayPalSubscriptionId = subscriptionId,
            PlanId = planId,
            Status = "APPROVAL_PENDING",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        return approveUrl;
    }

    // Changes an already-ACTIVE subscription to a different plan instead of creating a second
    // one — the same PayPal subscription id keeps billing, just for a different plan. Returns
    // an approve URL if PayPal needs the payer to confirm the new terms, same redirect pattern
    // as CreateSubscriptionAsync; null means it applied immediately, and Space.PlanId is
    // updated right away rather than waiting for a webhook that may never distinctly fire.
    public async Task<string?> ReviseSubscriptionAsync(Guid spaceId, Guid newPlanId, string returnUrl, string cancelUrl, CancellationToken ct)
    {
        var current = await db.SpaceSubscriptions
            .Where(x => x.SpaceId == spaceId && x.Status == "ACTIVE")
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            throw new InvalidOperationException($"Space {spaceId} has no ACTIVE PayPal subscription to revise.");
        }

        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == newPlanId, ct);
        var payPalPlanId = paymentProvider.IsLive ? plan.PayPalPlanIdLive : plan.PayPalPlanIdSandbox;
        if (payPalPlanId is null)
        {
            throw new InvalidOperationException(
                $"Plan {plan.Name} has no PayPal plan id for the {(paymentProvider.IsLive ? "live" : "sandbox")} environment — run EnsurePlansProvisionedAsync first.");
        }

        var approveUrl = await paymentProvider.ReviseSubscriptionAsync(current.PayPalSubscriptionId, payPalPlanId, returnUrl, cancelUrl, ct);

        current.PlanId = newPlanId;
        if (approveUrl is null)
        {
            await SetSpacePlanAsync(spaceId, newPlanId, ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return approveUrl;
    }

    // User-initiated from the console, not a webhook reaction — applied immediately
    // (Space.PlanId -> Free right away) since PayPal's cancel call is synchronous and final,
    // unlike Create/Revise there's no approval step to wait for. The
    // BILLING.SUBSCRIPTION.CANCELLED webhook that follows later just re-confirms the same
    // state, which HandleWebhookEventAsync already applies idempotently.
    public async Task CancelSubscriptionAsync(Guid spaceId, CancellationToken ct)
    {
        var current = await db.SpaceSubscriptions
            .Where(x => x.SpaceId == spaceId && BlockingStatuses.Contains(x.Status))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            throw new InvalidOperationException($"Space {spaceId} has no active or pending PayPal subscription to cancel.");
        }

        await paymentProvider.CancelSubscriptionAsync(current.PayPalSubscriptionId, "Cancelled by the space owner from the Tessera console.", ct);

        current.Status = "CANCELLED";
        await SetSpacePlanAsync(spaceId, SystemPlanIds.Free, ct);
    }

    public Task<SpaceSubscription?> GetForSpaceAsync(Guid spaceId, CancellationToken ct) =>
        db.SpaceSubscriptions.AsNoTracking()
            .Where(x => x.SpaceId == spaceId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

    // The single place PayPal's webhook vocabulary (docs/03-integrazioni.md) turns into a
    // Space's actual plan. Unknown subscription ids are logged and otherwise ignored rather
    // than thrown — a webhook for a subscription this environment doesn't know about (sandbox
    // event replayed against production, a race with CreateSubscriptionAsync's own SaveChanges)
    // isn't something the caller should fail a 200 OK response over.
    public async Task HandleWebhookEventAsync(string eventType, string payPalSubscriptionId, CancellationToken ct)
    {
        var subscription = await db.SpaceSubscriptions.FirstOrDefaultAsync(x => x.PayPalSubscriptionId == payPalSubscriptionId, ct);
        if (subscription is null)
        {
            logger.LogWarning("PayPal webhook {EventType} for unknown subscription {SubscriptionId}", eventType, payPalSubscriptionId);
            return;
        }

        switch (eventType)
        {
            case "BILLING.SUBSCRIPTION.ACTIVATED":
                subscription.Status = "ACTIVE";
                subscription.CurrentPeriodEnd = await paymentProvider.GetNextBillingTimeAsync(payPalSubscriptionId, ct);
                await SetSpacePlanAsync(subscription.SpaceId, subscription.PlanId, ct);
                break;

            case "BILLING.SUBSCRIPTION.SUSPENDED":
                // Deciso: nessun periodo di grazia — il downgrade non perde dati, solo funzioni
                // oltre le soglie del piano gratuito (docs/02-modello-dati.md).
                subscription.Status = "SUSPENDED";
                await SetSpacePlanAsync(subscription.SpaceId, SystemPlanIds.Free, ct);
                break;

            case "BILLING.SUBSCRIPTION.CANCELLED":
            case "BILLING.SUBSCRIPTION.EXPIRED":
                subscription.Status = eventType == "BILLING.SUBSCRIPTION.CANCELLED" ? "CANCELLED" : "EXPIRED";
                await SetSpacePlanAsync(subscription.SpaceId, SystemPlanIds.Free, ct);
                break;

            case "BILLING.SUBSCRIPTION.UPDATED":
                // Fires after a plan revision when PayPal required the payer's approval —
                // ReviseSubscriptionAsync already updated subscription.PlanId locally before
                // redirecting for approval, so this just confirms it and re-syncs Space.PlanId.
                // Empirically unverified against a real sandbox revise (docs/03-integrazioni.md).
                subscription.Status = "ACTIVE";
                subscription.CurrentPeriodEnd = await paymentProvider.GetNextBillingTimeAsync(payPalSubscriptionId, ct);
                await SetSpacePlanAsync(subscription.SpaceId, subscription.PlanId, ct);
                break;

            case "PAYMENT.SALE.COMPLETED":
                // A successful renewal — nothing about the plan changes, just when the next
                // one is due.
                subscription.CurrentPeriodEnd = await paymentProvider.GetNextBillingTimeAsync(payPalSubscriptionId, ct);
                await db.SaveChangesAsync(ct);
                break;

            default:
                logger.LogInformation("Unhandled PayPal webhook event type {EventType} for subscription {SubscriptionId}", eventType, payPalSubscriptionId);
                break;
        }
    }

    private async Task SetSpacePlanAsync(Guid spaceId, Guid planId, CancellationToken ct)
    {
        var space = await db.Spaces.FirstAsync(x => x.Id == spaceId, ct);
        space.PlanId = planId;
        await db.SaveChangesAsync(ct);
    }
}
