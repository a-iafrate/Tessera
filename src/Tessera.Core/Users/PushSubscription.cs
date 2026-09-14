namespace Tessera.Core.Users;

// One row per browser/device the user has enabled web push on (docs/13-piano-miglioramenti.md,
// C2) — a user can have several (phone, laptop), unlike ChannelIdentity's one-per-channel model.
// Endpoint is the push service's own delivery URL (unique per subscription, effectively an
// opaque unguessable secret); P256DH/Auth are the keys the browser generated for that endpoint,
// needed to encrypt the payload (RFC 8291) — none of this is a refresh token, so it lives in the
// database like any other row, not Key Vault (hard rule 4 is about OAuth tokens specifically).
public class PushSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = null!;
    public string P256dh { get; set; } = null!;
    public string Auth { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
