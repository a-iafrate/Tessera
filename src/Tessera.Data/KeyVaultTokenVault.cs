using Azure;
using Azure.Security.KeyVault.Secrets;
using Tessera.Core.Abstractions;

namespace Tessera.Data;

// Only registered when KeyVault:Name is configured (Program.cs mirrors the Application
// Insights conditional-registration pattern) — refresh tokens simply can't be stored until a
// Key Vault exists, and that's a clearer failure than a startup crash (hard rule 4,
// docs/07-compliance.md).
public sealed class KeyVaultTokenVault(SecretClient client) : ITokenVault
{
    public async Task SetAsync(string secretName, string value, CancellationToken ct) =>
        await client.SetSecretAsync(secretName, value, ct);

    public async Task<string?> GetAsync(string secretName, CancellationToken ct)
    {
        try
        {
            var secret = await client.GetSecretAsync(secretName, cancellationToken: ct);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string secretName, CancellationToken ct)
    {
        try
        {
            await client.StartDeleteSecretAsync(secretName, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }
}
