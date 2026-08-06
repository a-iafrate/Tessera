using Tessera.Core.Users;

namespace Tessera.Core.Abstractions;

public interface IChannelIdentityRepository
{
    // "This chat_id/external user corresponds to this User" (docs/02-modello-dati.md).
    // Null means the identity hasn't been linked yet — the caller falls back to a
    // default culture, and eventually to the linking flow (docs/03-integrazioni.md).
    Task<User?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct);

    // The reverse direction: where to send a proactive notification (reminders due, daily
    // digest) that isn't a reply to an inbound message, so there's no ChannelAddress to
    // reuse from the pipeline (docs/01-architettura.md, "il secondo worker").
    Task<IReadOnlyList<ChannelIdentity>> GetForUserAsync(Guid userId, CancellationToken ct);
}
