using Azure;
using Azure.Communication.Email;
using Tessera.Core.Abstractions;

namespace Tessera.Integrations;

// Azure Communication Services Email — connection string, same pattern as BlobStorage's
// AzureBlobStorage (Program.cs): the official SDK wraps a nontrivial per-request HMAC
// signature that isn't worth hand-rolling (unlike PayPal's plain bearer-token REST calls), and
// the connection string itself lives in App Service configuration / user-secrets like every
// other non-refresh-token secret in this app — no Key Vault involved (that's reserved for
// per-user OAuth refresh tokens, hard rule 4, docs/07-compliance.md).
public sealed class AzureEmailClient(string connectionString, string senderAddress) : IEmailSender
{
    private readonly EmailClient client = new(connectionString);

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        var message = new EmailMessage(
            senderAddress,
            new EmailRecipients([new EmailAddress(toEmail)]),
            new EmailContent(subject) { Html = htmlBody });

        // Started, not Completed — confirms the send was accepted, doesn't block on final
        // delivery status (same "fire and confirm" spirit as every other outbound call here).
        await client.SendAsync(WaitUntil.Started, message, ct);
    }
}
