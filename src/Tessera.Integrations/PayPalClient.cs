using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tessera.Core.Abstractions;

namespace Tessera.Integrations;

// PayPal Subscriptions REST API, called by hand rather than an SDK — same reasoning as
// LinkedAccountService for calendars: no SDK-owned token cache to fight with, and this app's
// own token caching stays visible and simple (docs/03-integrazioni.md). Unlike calendar
// linking there is no per-user OAuth here: Tessera itself is the merchant, authenticated via a
// single app-level client_credentials token, so this client needs no Key Vault, no refresh
// token, and no LinkedAccount-style per-user record.
public sealed class PayPalClient(IHttpClientFactory httpClientFactory, string clientId, string clientSecret, string webhookId, bool isLive)
    : IPaymentProvider
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedAccessToken;
    private DateTimeOffset tokenExpiresAt;

    public bool IsLive => isLive;

    private string BaseUrl => isLive ? "https://api-m.paypal.com" : "https://api-m.sandbox.paypal.com";

    // Creates the "Tessera" product once, reusing it on every later call — PayPal's Catalog
    // Products API has no natural idempotency key, so this searches by name first rather than
    // risking a duplicate product on every deploy that happens to run provisioning again.
    public async Task<string> EnsureProductAsync(CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/catalogs/products?page_size=20&total_required=false");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var listResponse = await client.SendAsync(listRequest, ct);
        listResponse.EnsureSuccessStatusCode();
        var listPayload = await listResponse.Content.ReadFromJsonAsync<ProductListResponse>(cancellationToken: ct);
        var existing = listPayload?.Products?.FirstOrDefault(x => x.Name == "Tessera");
        if (existing is not null)
        {
            return existing.Id;
        }

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/catalogs/products");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        createRequest.Content = JsonContent.Create(new
        {
            name = "Tessera",
            description = "Tessera — assistente condiviso per la famiglia",
            type = "SERVICE",
            category = "SOFTWARE",
        });
        using var createResponse = await client.SendAsync(createRequest, ct);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("PayPal product creation returned an empty response.");
        return created.Id;
    }

    // One PayPal billing plan per paid SubscriptionPlan tier (docs/02-modello-dati.md) — Free
    // has no PayPal plan, so this is only ever called for Basic/Plus/Family.
    //
    // payment_failure_threshold: 1 — matches the product decision that a suspended
    // subscription downgrades the Space to Free immediately, with no grace period (there's no
    // data loss on downgrade, only reduced limits), so PayPal should suspend on the first
    // failed payment rather than retry silently for days first.
    public async Task<string> CreatePlanAsync(string productId, string planName, decimal monthlyPrice, string currency, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/billing/plans");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            product_id = productId,
            name = planName,
            description = $"Piano Tessera {planName}",
            billing_cycles = new object[]
            {
                new
                {
                    frequency = new { interval_unit = "MONTH", interval_count = 1 },
                    tenure_type = "REGULAR",
                    sequence = 1,
                    total_cycles = 0,
                    pricing_scheme = new
                    {
                        fixed_price = new { value = monthlyPrice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), currency_code = currency },
                    },
                },
            },
            payment_preferences = new
            {
                auto_bill_outstanding = true,
                payment_failure_threshold = 1,
            },
        });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<PlanResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("PayPal plan creation returned an empty response.");
        return payload.Id;
    }

    // Returns the id PayPal assigned to the new subscription plus the "approve" link the
    // browser must be redirected to — PayPal doesn't take payment on this call, only on the
    // user's approval on their own site (docs/03-integrazioni.md).
    public async Task<(string SubscriptionId, string ApproveUrl)> CreateSubscriptionAsync(
        string payPalPlanId, string returnUrl, string cancelUrl, string locale, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/billing/subscriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            plan_id = payPalPlanId,
            application_context = new
            {
                brand_name = "Tessera",
                locale,
                return_url = returnUrl,
                cancel_url = cancelUrl,
                user_action = "SUBSCRIBE_NOW",
            },
        });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SubscriptionResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("PayPal subscription creation returned an empty response.");

        var approveUrl = payload.Links?.FirstOrDefault(x => x.Rel == "approve")?.Href
            ?? throw new InvalidOperationException($"PayPal subscription {payload.Id} response had no 'approve' link.");
        return (payload.Id, approveUrl);
    }

    // Used after ACTIVATED/PAYMENT.SALE.COMPLETED events to read the next renewal date —
    // that detail isn't in the webhook payload itself, only on the subscription resource.
    public async Task<DateTimeOffset?> GetNextBillingTimeAsync(string payPalSubscriptionId, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/billing/subscriptions/{payPalSubscriptionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SubscriptionDetailsResponse>(cancellationToken: ct);
        return payload?.BillingInfo?.NextBillingTime;
    }

    // The only trustworthy way to know a webhook call actually came from PayPal — the headers
    // plus the raw event body get sent back to PayPal itself for verification
    // (docs/03-integrazioni.md), unlike WhatsApp/Telegram where the signature is checked
    // locally. webhookEvent must be the exact JSON body PayPal sent, unmodified.
    public async Task<bool> VerifyWebhookSignatureAsync(
        string transmissionId, string transmissionTime, string certUrl, string authAlgo, string transmissionSig,
        JsonElement webhookEvent, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/notifications/verify-webhook-signature");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            auth_algo = authAlgo,
            cert_url = certUrl,
            transmission_id = transmissionId,
            transmission_sig = transmissionSig,
            transmission_time = transmissionTime,
            webhook_id = webhookId,
            webhook_event = webhookEvent,
        });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<VerifyWebhookResponse>(cancellationToken: ct);
        return payload?.VerificationStatus == "SUCCESS";
    }

    // App-level token (client_credentials, no end user involved), cached until shortly before
    // it expires — PayPal's own tokens last several hours, and re-requesting one on every API
    // call would be wasteful for no benefit (unlike a calendar refresh token, this isn't
    // per-user secret material that hard rule 4 requires routing through Key Vault).
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (cachedAccessToken is not null && DateTimeOffset.UtcNow < tokenExpiresAt)
        {
            return cachedAccessToken;
        }

        await tokenLock.WaitAsync(ct);
        try
        {
            if (cachedAccessToken is not null && DateTimeOffset.UtcNow < tokenExpiresAt)
            {
                return cachedAccessToken;
            }

            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("PayPal token endpoint returned an empty response.");

            cachedAccessToken = payload.AccessToken;
            tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn - 60);
            return cachedAccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ProductListResponse([property: JsonPropertyName("products")] List<ProductResponse>? Products);

    private sealed record ProductResponse([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name);

    private sealed record PlanResponse([property: JsonPropertyName("id")] string Id);

    private sealed record SubscriptionResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("links")] List<LinkResponse>? Links);

    private sealed record LinkResponse(
        [property: JsonPropertyName("rel")] string Rel,
        [property: JsonPropertyName("href")] string Href);

    private sealed record SubscriptionDetailsResponse([property: JsonPropertyName("billing_info")] BillingInfoResponse? BillingInfo);

    private sealed record BillingInfoResponse([property: JsonPropertyName("next_billing_time")] DateTimeOffset? NextBillingTime);

    private sealed record VerifyWebhookResponse([property: JsonPropertyName("verification_status")] string VerificationStatus);
}
