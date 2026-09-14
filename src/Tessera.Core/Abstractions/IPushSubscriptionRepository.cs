using Tessera.Core.Users;

namespace Tessera.Core.Abstractions;

public interface IPushSubscriptionRepository
{
    Task<IReadOnlyList<PushSubscription>> GetForUserAsync(Guid userId, CancellationToken ct);

    // Upsert by Endpoint: re-subscribing the same browser (e.g. after clearing the service
    // worker) sends the same endpoint again — treat it as a refresh, not a duplicate row.
    Task AddAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken ct);

    // Called both when the user explicitly disables push and when WebChannel finds a
    // subscription the push service has rejected as gone (docs/13-piano-miglioramenti.md, C2).
    Task RemoveAsync(string endpoint, CancellationToken ct);
}
