using Tessera.Core.Users;

namespace Tessera.Core.Abstractions;

public interface IChannelIdentityRepository
{
    // "This chat_id/external user corresponds to this User" (docs/02-modello-dati.md).
    // Null means the identity hasn't been linked yet — the caller falls back to a
    // default culture, and eventually to the linking flow (docs/03-integrazioni.md).
    Task<User?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct);
}
