using Microsoft.EntityFrameworkCore;
using Tessera.Core.Conversations;
using Tessera.Core.Spaces;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data.Tests;

// The five-step "which space?" precedence chain (docs/02-modello-dati.md,
// docs/13-piano-miglioramenti.md F4) — resolved per resource, not per user, so most tests here
// deliberately give the same user different access on different resources to prove that.
public class SpaceResolverTests : IDisposable
{
    private readonly TestDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;

    private readonly Guid userId = Guid.NewGuid();

    public void Dispose() => testDb.Dispose();

    private SpaceResolver CreateResolver()
    {
        var memberships = new MembershipRepository(Db, testDb.Cache);
        var accessPolicy = new AccessPolicy(memberships);
        return new SpaceResolver(Db, accessPolicy);
    }

    private async Task<Space> AddSpaceAsync(string name)
    {
        var space = new Space
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = userId,
            PlanId = SystemPlanIds.Free,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Db.Spaces.Add(space);
        await Db.SaveChangesAsync();
        return space;
    }

    private async Task AddMembershipAsync(Guid spaceId, ResourceKind resource, AccessLevel level)
    {
        var membership = new Membership { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = userId, JoinedAt = DateTimeOffset.UtcNow };
        membership.Permissions.Add(new MembershipPermission { Resource = resource, Level = level });
        Db.Memberships.Add(membership);
        await Db.SaveChangesAsync();
    }

    private async Task AddUserAsync(Guid? defaultSpaceId = null)
    {
        Db.DomainUsers.Add(new DomainUser { Id = userId, Email = "test@example.com", DefaultSpaceId = defaultSpaceId, CreatedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullSpace_WhenUserHasNoAccessibleSpaceForResource()
    {
        var space = await AddSpaceAsync("Casa");
        await AddUserAsync();
        // Read is not enough for the Write this call requires — zero accessible spaces, so this
        // must return before ever reaching step 3's DefaultSpaceId lookup.
        await AddMembershipAsync(space.Id, ResourceKind.ShoppingList, AccessLevel.Read);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, messageText: null, CancellationToken.None);

        Assert.Null(result.SpaceId);
        Assert.False(result.IsAmbiguous);
        Assert.Empty(result.AmbiguousCandidates);
    }

    [Fact]
    public async Task ResolveAsync_Step1_ReturnsExplicitSpace_WhenMessageNamesAnAccessibleSpace()
    {
        await AddUserAsync();
        var casa = await AddSpaceAsync("Casa");
        await AddMembershipAsync(casa.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, "aggiungi latte in Casa", CancellationToken.None);

        Assert.Equal(casa.Id, result.SpaceId);
        Assert.Equal("aggiungi latte", result.RemainingText);
        Assert.Null(result.PermissionDeniedSpaceId);

        // Naming a space also sets it active for the TTL window (SetActiveSpaceAsync).
        var state = await Db.ConversationStates.SingleAsync(s => s.UserId == userId);
        Assert.Equal(casa.Id, state.ActiveSpaceId);
        Assert.True(state.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(20));
    }

    [Fact]
    public async Task ResolveAsync_Step1_ReportsPermissionDenied_WhenNamedSpaceLacksTheRequiredLevel_AndFallsBackToAnAccessibleSpace()
    {
        await AddUserAsync();
        var casa = await AddSpaceAsync("Casa");
        await AddMembershipAsync(casa.Id, ResourceKind.ShoppingList, AccessLevel.Read); // not enough for Write
        var ufficio = await AddSpaceAsync("Ufficio");
        await AddMembershipAsync(ufficio.Id, ResourceKind.ShoppingList, AccessLevel.Write); // the only accessible fallback
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, "aggiungi latte in Casa", CancellationToken.None);

        Assert.Equal(casa.Id, result.PermissionDeniedSpaceId);
        Assert.Equal(ufficio.Id, result.SpaceId);
        Assert.Equal("aggiungi latte", result.RemainingText);
    }

    [Fact]
    public async Task ResolveAsync_Step2_UsesActiveConversationState_WhenWithinTtl()
    {
        await AddUserAsync();
        var spaceA = await AddSpaceAsync("Casa");
        await AddMembershipAsync(spaceA.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var spaceB = await AddSpaceAsync("Ufficio");
        await AddMembershipAsync(spaceB.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        Db.ConversationStates.Add(new ConversationState
        {
            Id = Guid.NewGuid(), UserId = userId, ActiveSpaceId = spaceB.Id,
            UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await Db.SaveChangesAsync();
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, messageText: null, CancellationToken.None);

        Assert.Equal(spaceB.Id, result.SpaceId);
    }

    [Fact]
    public async Task ResolveAsync_Step2_IgnoresExpiredConversationState_FallsThroughToDefaultSpace()
    {
        var spaceA = await AddSpaceAsync("Casa");
        var spaceB = await AddSpaceAsync("Ufficio");
        await AddUserAsync(defaultSpaceId: spaceA.Id);
        await AddMembershipAsync(spaceA.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        await AddMembershipAsync(spaceB.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        Db.ConversationStates.Add(new ConversationState
        {
            Id = Guid.NewGuid(), UserId = userId, ActiveSpaceId = spaceB.Id,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-40), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        await Db.SaveChangesAsync();
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, messageText: null, CancellationToken.None);

        Assert.Equal(spaceA.Id, result.SpaceId);
    }

    [Fact]
    public async Task ResolveAsync_Step3_UsesDefaultSpaceId_WhenAccessible()
    {
        var spaceA = await AddSpaceAsync("Casa");
        var spaceB = await AddSpaceAsync("Ufficio");
        await AddUserAsync(defaultSpaceId: spaceB.Id);
        await AddMembershipAsync(spaceA.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        await AddMembershipAsync(spaceB.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, messageText: null, CancellationToken.None);

        Assert.Equal(spaceB.Id, result.SpaceId);
    }

    [Fact]
    public async Task ResolveAsync_Step4_ResolvesTheOnlySpaceWithEnoughPermission_PerResourceIndependently()
    {
        await AddUserAsync(); // no DefaultSpaceId — nothing to short-circuit steps 2/3
        var spaceA = await AddSpaceAsync("Casa");
        await AddMembershipAsync(spaceA.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var spaceB = await AddSpaceAsync("Ufficio");
        await AddMembershipAsync(spaceB.Id, ResourceKind.Expenses, AccessLevel.Write);
        var resolver = CreateResolver();

        var shoppingResult = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, messageText: null, CancellationToken.None);
        var expensesResult = await resolver.ResolveAsync(userId, ResourceKind.Expenses, AccessLevel.Write, messageText: null, CancellationToken.None);

        Assert.Equal(spaceA.Id, shoppingResult.SpaceId);
        Assert.Equal(spaceB.Id, expensesResult.SpaceId);
    }

    [Fact]
    public async Task ResolveAsync_Step5_ReturnsAmbiguous_WhenMultipleSpacesQualifyWithNoTiebreaker()
    {
        await AddUserAsync();
        var spaceA = await AddSpaceAsync("Casa");
        await AddMembershipAsync(spaceA.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var spaceB = await AddSpaceAsync("Ufficio");
        await AddMembershipAsync(spaceB.Id, ResourceKind.ShoppingList, AccessLevel.Write);
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(userId, ResourceKind.ShoppingList, AccessLevel.Write, "aggiungi latte", CancellationToken.None);

        Assert.Null(result.SpaceId);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.AmbiguousCandidates.Count);
        Assert.Contains(spaceA.Id, result.AmbiguousCandidates);
        Assert.Contains(spaceB.Id, result.AmbiguousCandidates);
        Assert.Equal("aggiungi latte", result.RemainingText);
    }

    [Fact]
    public async Task SetActiveSpaceAsync_UpsertsASingleRowPerUser_NotOnePerCall()
    {
        var spaceA = await AddSpaceAsync("Casa");
        var spaceB = await AddSpaceAsync("Ufficio");
        var resolver = CreateResolver();

        await resolver.SetActiveSpaceAsync(userId, spaceA.Id, CancellationToken.None);
        await resolver.SetActiveSpaceAsync(userId, spaceB.Id, CancellationToken.None);

        var state = await Db.ConversationStates.SingleAsync(s => s.UserId == userId);
        Assert.Equal(spaceB.Id, state.ActiveSpaceId);
    }
}
