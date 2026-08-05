using Tessera.Core.Spaces;

namespace Tessera.Core.Abstractions;

public interface IAccessPolicy
{
    Task<bool> CanAsync(Guid userId, Guid spaceId, ResourceKind resource, AccessLevel required, CancellationToken ct);
}
