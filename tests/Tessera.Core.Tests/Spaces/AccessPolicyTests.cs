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

    // A departed member has no Membership row at all — archived instead, via
    // MembershipArchive (docs/02-modello-dati.md) — so from this class's point of view "former
    // member" and "no membership" are the same code path, already covered above. There's no
    // separate branch here to test for it.
    [Fact]
    public async Task CanAsync_ReturnsFalse_WhenUserHasNoMembership_SameAsAFormerMember()
    {
        var policy = new AccessPolicy(new FakeMembershipRepository(membership: null));

        foreach (var resource in Enum.GetValues<ResourceKind>())
        {
            var result = await policy.CanAsync(UserId, SpaceId, resource, AccessLevel.Availability, CancellationToken.None);
            Assert.False(result);
        }
    }

    [Theory]
    [MemberData(nameof(AllResourceKinds))]
    public async Task CanAsync_ReturnsTrue_WhenUserIsOwner_ForEveryResourceAtEveryLevel(ResourceKind resource)
    {
        // No Permissions at all — IsOwner must short-circuit regardless of resource or level,
        // exactly the bypass CanAsync's second branch implements.
        var membership = new Membership { SpaceId = SpaceId, UserId = UserId, IsOwner = true };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        foreach (var required in Enum.GetValues<AccessLevel>())
        {
            var result = await policy.CanAsync(UserId, SpaceId, resource, required, CancellationToken.None);
            Assert.True(result, $"Owner should have {required} on {resource}");
        }
    }

    [Fact]
    public async Task CanAsync_ReturnsFalse_WhenNoPermissionExistsForResource()
    {
        var membership = new Membership { SpaceId = SpaceId, UserId = UserId, IsOwner = false };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.Calendar, AccessLevel.Availability, CancellationToken.None);

        Assert.False(result);
    }

    // Not a corner case that "should" be true for a lenient reading of None — CanAsync requires
    // a MembershipPermission row to exist at all, independent of what's required. Locked in so a
    // future "helpful" special case for None doesn't silently start granting access to a
    // resource nobody ever explicitly permissioned.
    [Fact]
    public async Task CanAsync_ReturnsFalse_ForResourceWithNoPermissionRow_EvenWhenRequiredLevelIsNone()
    {
        var membership = new Membership { SpaceId = SpaceId, UserId = UserId, IsOwner = false, Permissions = [] };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        var result = await policy.CanAsync(UserId, SpaceId, ResourceKind.Notes, AccessLevel.None, CancellationToken.None);

        Assert.False(result);
    }

    // Exhaustive 5x5 grid over AccessLevel — generated, not hand-transcribed, so there's no
    // copy/paste risk in the expectations themselves. Independently restates "granted >=
    // required" rather than re-running CanAsync's own comparison, so it still catches the
    // classic bug this class exists to prevent: the comparison silently inverted
    // (required >= permission.Level) would flip every asymmetric row here.
    [Theory]
    [MemberData(nameof(LevelComparisonMatrix))]
    public async Task CanAsync_ComparesGrantedLevelAgainstRequiredLevel_AcrossEveryAccessLevelPair(
        AccessLevel granted, AccessLevel required, bool expected)
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

    // The scenario hard rule 15 / CLAUDE.md's testing note is actually about: a permission
    // granted on one resource must never leak into another. Admin on ShoppingList is the
    // strongest possible permission short of IsOwner — if it leaked anywhere, this would catch
    // it on every other ResourceKind at the weakest possible required level.
    [Fact]
    public async Task CanAsync_DoesNotLeakAPermission_IntoAnyOtherResource()
    {
        var membership = new Membership
        {
            SpaceId = SpaceId,
            UserId = UserId,
            IsOwner = false,
            Permissions = [new MembershipPermission { Resource = ResourceKind.ShoppingList, Level = AccessLevel.Admin }],
        };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        foreach (var otherResource in Enum.GetValues<ResourceKind>().Where(r => r != ResourceKind.ShoppingList))
        {
            var result = await policy.CanAsync(UserId, SpaceId, otherResource, AccessLevel.Availability, CancellationToken.None);
            Assert.False(result, $"ShoppingList Admin permission leaked into {otherResource}");
        }
    }

    // Companion to the leak test above: when a membership legitimately has several permissions
    // on several resources, each one must be evaluated on its own — not "any permission at a
    // high enough level unlocks everything", and not "the lowest of the several caps them all".
    [Fact]
    public async Task CanAsync_EvaluatesEachResourceAgainstItsOwnPermission_WhenMembershipHasSeveral()
    {
        var membership = new Membership
        {
            SpaceId = SpaceId,
            UserId = UserId,
            IsOwner = false,
            Permissions =
            [
                new MembershipPermission { Resource = ResourceKind.ShoppingList, Level = AccessLevel.Write },
                new MembershipPermission { Resource = ResourceKind.Expenses, Level = AccessLevel.Read },
                new MembershipPermission { Resource = ResourceKind.Calendar, Level = AccessLevel.Availability },
            ],
        };
        var policy = new AccessPolicy(new FakeMembershipRepository(membership));

        Assert.True(await policy.CanAsync(UserId, SpaceId, ResourceKind.ShoppingList, AccessLevel.Write, CancellationToken.None));
        Assert.True(await policy.CanAsync(UserId, SpaceId, ResourceKind.Expenses, AccessLevel.Read, CancellationToken.None));
        Assert.False(await policy.CanAsync(UserId, SpaceId, ResourceKind.Expenses, AccessLevel.Write, CancellationToken.None));
        Assert.True(await policy.CanAsync(UserId, SpaceId, ResourceKind.Calendar, AccessLevel.Availability, CancellationToken.None));
        Assert.False(await policy.CanAsync(UserId, SpaceId, ResourceKind.Calendar, AccessLevel.Read, CancellationToken.None));
        // Reminders has no row on this membership at all — must fail, not fall back to any of
        // the other three permissions.
        Assert.False(await policy.CanAsync(UserId, SpaceId, ResourceKind.Reminders, AccessLevel.Availability, CancellationToken.None));
    }

    public static IEnumerable<object[]> AllResourceKinds() =>
        Enum.GetValues<ResourceKind>().Select(resource => new object[] { resource });

    public static IEnumerable<object[]> LevelComparisonMatrix()
    {
        foreach (var granted in Enum.GetValues<AccessLevel>())
        {
            foreach (var required in Enum.GetValues<AccessLevel>())
            {
                yield return [granted, required, granted >= required];
            }
        }
    }

    private sealed class FakeMembershipRepository(Membership? membership) : IMembershipRepository
    {
        public Task<Membership?> FindAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
            Task.FromResult(membership);
    }
}
