namespace Tessera.Core.Users;

// "This user has authorized Google/Microsoft" — distinct from ChannelIdentity (which bot chat_id
// maps to this user) and from console login (a user can sign in with Google without ever
// authorizing Google Calendar; different OAuth flows, different scopes) (docs/02-modello-dati.md).
public class LinkedAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ProviderKind Provider { get; set; }
    public string ProviderUserId { get; set; } = null!;
    public string? ProviderEmail { get; set; }

    // Only the Key Vault secret name lives here — never the refresh token itself
    // (hard rule 4, docs/07-compliance.md).
    public string TokenSecretName { get; set; } = null!;
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}
