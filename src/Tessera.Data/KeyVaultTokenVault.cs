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
    // A prior unlink soft-deletes the secret (Key Vault's mandatory retention, not something
    // DeleteAsync can opt out of) — relinking before that retention window passes reuses the
    // same deterministic name ("oauth-google-{userId}") and Key Vault refuses with 409 until
    // the name is recovered or purged. Recovering (which restores the old version) and then
    // immediately overwriting is the correct response: the caller is about to replace the
    // value anyway.
    public async Task SetAsync(string secretName, string value, CancellationToken ct)
    {
        try
        {
            await client.SetSecretAsync(secretName, value, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            var recoverOperation = await client.StartRecoverDeletedSecretAsync(secretName, ct);
            await recoverOperation.WaitForCompletionAsync(ct);
            await client.SetSecretAsync(secretName, value, ct);
        }
    }

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
