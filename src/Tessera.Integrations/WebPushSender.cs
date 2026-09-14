using System.Net;
using System.Text.Json;
using Tessera.Core.Abstractions;
using WebPush;

namespace Tessera.Integrations;

// VAPID (RFC 8292) + payload encryption (RFC 8291) — the official web-push-libs/web-push-csharp
// port, same reasoning as AzureEmailClient reusing the ACS SDK: reimplementing the crypto isn't
// worth it. Connection string-equivalent secret here is the VAPID key pair, generated once
// (WebPush.VapidHelper.GenerateVapidKeys()) and provisioned like every other non-refresh-token
// secret (docs/08-setup-sviluppo.md) — nothing here is a per-user OAuth token, so Key Vault
// doesn't apply (hard rule 4).
public sealed class WebPushSender(string vapidSubject, string vapidPublicKey, string vapidPrivateKey) : IPushSender
{
    private readonly WebPushClient client = new();
    private readonly VapidDetails vapidDetails = new(vapidSubject, vapidPublicKey, vapidPrivateKey);

    public async Task SendAsync(string endpoint, string p256dh, string auth, string title, string body, string url, CancellationToken ct)
    {
        var subscription = new PushSubscription(endpoint, p256dh, auth);
        var payload = JsonSerializer.Serialize(new { title, body, url });

        try
        {
            await client.SendNotificationAsync(subscription, payload, vapidDetails, ct);
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // The push service itself says this subscription no longer exists (browser
            // uninstalled, user cleared site data, the works) — translate to the
            // library-agnostic exception WebChannel knows how to act on.
            throw new PushSubscriptionGoneException(endpoint);
        }
    }
}
