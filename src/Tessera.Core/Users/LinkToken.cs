namespace Tessera.Core.Users;

// A LinkToken is itself an authentication: whoever presents it associates their chat with
// the target account. Single use, short-lived (docs/02-modello-dati.md, docs/07-compliance.md).
public class LinkToken
{
    public Guid Id { get; set; }
    public string Token { get; set; } = null!;
    public Guid UserId { get; set; }
    public string ChannelName { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
