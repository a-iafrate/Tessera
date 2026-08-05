using Tessera.Core.Abstractions;

namespace Tessera.Core.Spaces;

public sealed class AccessPolicy(IMembershipRepository memberships) : IAccessPolicy
{
    public async Task<bool> CanAsync(Guid userId, Guid spaceId, ResourceKind resource, AccessLevel required, CancellationToken ct)
    {
        var membership = await memberships.FindAsync(userId, spaceId, ct);
        if (membership is null)
        {
            return false;
        }

        if (membership.IsOwner)
        {
            return true;
        }

        var permission = membership.Permissions.FirstOrDefault(p => p.Resource == resource);
        return permission is not null && permission.Level >= required;
    }
}
