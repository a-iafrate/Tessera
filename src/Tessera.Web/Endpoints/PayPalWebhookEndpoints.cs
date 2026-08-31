using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Data;

namespace Tessera.Web.Endpoints;

// Hard rule 6: webhooks return 200 OK quickly and deduplicate on the provider's own message
// id — here PayPal's event_id, reusing the same ProcessedMessage table Telegram dedupes
// through (ChannelName "paypal" is not really a channel in the IChannel sense, but the shape
// of the problem — "have I already handled this provider's event id" — is identical, and a
// second near-duplicate table would only exist to hold the same two columns).
public static class PayPalWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPayPalWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/hooks/paypal", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context, IPaymentProvider paymentProvider, PayPalSubscriptionService subscriptions,
        TesseraDbContext db, ILogger<PayPalSubscriptionService> logger, CancellationToken ct)
    {
        string rawBody;
        using (var reader = new StreamReader(context.Request.Body))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var eventIdElement) || !root.TryGetProperty("event_type", out var eventTypeElement))
        {
            // Not a shape PayPal's own webhooks send — nothing to verify or process, but still
            // 200 so PayPal doesn't retry a request that will never parse differently.
            return Results.Ok();
        }

        var eventId = eventIdElement.GetString()!;
        var eventType = eventTypeElement.GetString()!;

        var headers = context.Request.Headers;
        var verified = await paymentProvider.VerifyWebhookSignatureAsync(
            headers["PAYPAL-TRANSMISSION-ID"].ToString(),
            headers["PAYPAL-TRANSMISSION-TIME"].ToString(),
            headers["PAYPAL-CERT-URL"].ToString(),
            headers["PAYPAL-AUTH-ALGO"].ToString(),
            headers["PAYPAL-TRANSMISSION-SIG"].ToString(),
            root, ct);

        if (!verified)
        {
            logger.LogWarning("PayPal webhook signature verification failed for event {EventId} ({EventType})", eventId, eventType);
            return Results.Unauthorized();
        }

        var alreadyProcessed = await db.ProcessedMessages.AnyAsync(x => x.ChannelName == "paypal" && x.ProviderMessageId == eventId, ct);
        if (alreadyProcessed)
        {
            return Results.Ok();
        }

        db.ProcessedMessages.Add(new ProcessedMessage { ChannelName = "paypal", ProviderMessageId = eventId, ProcessedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);

        if (root.TryGetProperty("resource", out var resource)
            && ExtractSubscriptionId(eventType, resource) is { } subscriptionId)
        {
            try
            {
                await subscriptions.HandleWebhookEventAsync(eventType, subscriptionId, ct);
            }
            catch (Exception ex)
            {
                // The event is already recorded as processed above — retrying it wouldn't
                // change the outcome of a genuine bug, and PayPal retrying forever on our own
                // exception is worse than one missed state transition surfacing in the logs.
                logger.LogError(ex, "Failed to apply PayPal webhook event {EventId} ({EventType})", eventId, eventType);
            }
        }

        return Results.Ok();
    }

    // BILLING.SUBSCRIPTION.* events carry the subscription id as resource.id; PAYMENT.SALE.*
    // events carry it as resource.billing_agreement_id instead — different resource shapes for
    // the same underlying subscription (docs/03-integrazioni.md).
    private static string? ExtractSubscriptionId(string eventType, JsonElement resource)
    {
        if (eventType.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal))
        {
            return resource.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        if (eventType.StartsWith("PAYMENT.SALE.", StringComparison.Ordinal))
        {
            return resource.TryGetProperty("billing_agreement_id", out var billingAgreementId) ? billingAgreementId.GetString() : null;
        }

        return null;
    }
}
