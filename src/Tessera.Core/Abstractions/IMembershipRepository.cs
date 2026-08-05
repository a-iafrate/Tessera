using Tessera.Core.Spaces;

namespace Tessera.Core.Abstractions;

public interface IMembershipRepository
{
    Task<Membership?> FindAsync(Guid userId, Guid spaceId, CancellationToken ct);
}
