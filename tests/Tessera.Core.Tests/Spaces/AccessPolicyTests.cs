using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;

namespace Tessera.Core.Tests.Spaces;

public class AccessPolicyTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SpaceId = Guid.NewGuid();

    [Fact]
    public async Task CanAsync_ReturnsFalse_WhenUserHasNoMembership()
    {
        var policy = new AccessPolicy(new FakeMembershipRepository(membership: null));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.ShoppingList, AccessLevel.Read, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task CanAsync_ReturnsTrue_WhenUserIsOwner_RegardlessOfExplicitPermission()
    {
        var membership = new Membership { SpaceId = SpaceId, UserId = UserId, IsOwner = true };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.Expenses, AccessLevel.Admin, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task CanAsync_ReturnsFalse_WhenNoPermissionExistsForResource()
    {
        var membership = new Membership { SpaceId = SpaceId, UserId = UserId, IsOwner = false };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.Calendar, AccessLevel.Availability, CancellationToken.None);

        Assert.False(result);
    }

    [Theory]
    [InlineData(AccessLevel.Write, AccessLevel.Read, true)]
    [InlineData(AccessLevel.Read, AccessLevel.Write, false)]
    [InlineData(AccessLevel.Write, AccessLevel.Write, true)]
    public async Task CanAsync_ComparesGrantedLevelAgainstRequiredLevel(AccessLevel granted, AccessLevel required, bool expected)
    {
        var membership = new Membership
        {
            SpaceId = SpaceId,
            UserId = UserId,
            IsOwner = false,
            Permissions = [new MembershipPermission { Resource = ResourceKind.ShoppingList, Level = granted }],
        };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.ShoppingList, required, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    private sealed class FakeMembershipRepository(Membership? membership) : IMembershipRepository
    {
        public Task<Membership?> FindAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
            Task.FromResult(membership);
    }
}
