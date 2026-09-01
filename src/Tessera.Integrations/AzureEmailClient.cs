using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Tessera.Core.Abstractions;

namespace Tessera.Integrations;

// Azure Communication Services Email REST API, called by hand rather than the
// Azure.Communication.Email SDK — same reasoning as PayPalClient/GoogleCalendarClient: no
// SDK-owned client to carry as a dependency for what's a single POST. Authenticates via the
// same Managed Identity already used for Key Vault (Program.cs), not a connection string, so
// there's no new secret to store anywhere (docs/03-integrazioni.md). TokenCredential caches
// and refreshes its own tokens internally — unlike PayPal's client_credentials flow, this
// client doesn't need to hand-roll that itself.
public sealed class AzureEmailClient(string endpoint, string senderAddress, TokenCredential credential, IHttpClientFactory httpClientFactory)
    : IEmailSender
{
    private static readonly string[] Scopes = ["https://communication.azure.com/.default"];

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        var client = httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.TrimEnd('/')}/emails:send?api-version=2023-03-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new
        {
            senderAddress,
            content = new { subject, html = htmlBody },
            recipients = new { to = new[] { new { address = toEmail } } },
        });

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
