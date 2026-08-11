namespace Tessera.Core.Abstractions;

// The only place a refresh token is allowed to live is Key Vault (hard rule 4,
// docs/07-compliance.md) — the database only ever holds the secret name this returns/expects.
public interface ITokenVault
{
    Task SetAsync(string secretName, string value, CancellationToken ct);

    Task<string?> GetAsync(string secretName, CancellationToken ct);

    Task DeleteAsync(string secretName, CancellationToken ct);
}
