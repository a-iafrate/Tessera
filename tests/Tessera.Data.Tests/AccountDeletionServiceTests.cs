using Microsoft.EntityFrameworkCore;
using Tessera.Core.Conversations;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data.Tests;

// Erasure here is pseudonymization, not cascading deletion (docs/02-modello-dati.md,
// docs/07-compliance.md): content in a shared space stays so other members' history and totals
// remain intact, but every identifying field on the deleted account is stripped. Only the
// personal space — which by construction nobody else is a member of — is deleted outright,
// content and all (docs/13-piano-miglioramenti.md, F4).
public class AccountDeletionServiceTests : IDisposable
{
    private readonly TestDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly AccountDeletionService deletion;

    public AccountDeletionServiceTests()
    {
        var spaces = new SpaceService(Db, testDb.Cache);
        deletion = new AccountDeletionService(Db, spaces, testDb.Cache);
    }

    public void Dispose() => testDb.Dispose();

    private async Task<DomainUser> AddUserAsync(Guid? defaultSpaceId = null)
    {
        var user = new DomainUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid()}@example.com", DefaultSpaceId = defaultSpaceId, CreatedAt = DateTimeOffset.UtcNow };
        Db.DomainUsers.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task<Space> AddSpaceAsync(Guid ownerId, bool isPersonal)
    {
        var space = new Space { Id = Guid.NewGuid(), Name = isPersonal ? "Personale" : "Casa", OwnerId = ownerId, IsPersonal = isPersonal, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow };
        Db.Spaces.Add(space);
        // SpaceConfiguration.HasDefaultValue(true) on IsPersonal means EF would otherwise treat
        // "false" (the CLR default for bool) as "not set" and let the store default (true) win —
        // forcing Modified makes the explicit false actually reach a shared-space test.
        Db.Entry(space).Property(x => x.IsPersonal).IsModified = true;
        await Db.SaveChangesAsync();
        return space;
    }

    private async Task<Membership> AddMembershipAsync(Guid spaceId, Guid userId, bool isOwner, DateTimeOffset joinedAt)
    {
        var membership = new Membership { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = userId, IsOwner = isOwner, JoinedAt = joinedAt };
        Db.Memberships.Add(membership);
        await Db.SaveChangesAsync();
        return membership;
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheUserRow()
    {
        var user = await AddUserAsync();

        await deletion.DeleteAsync(user.Id, CancellationToken.None);

        Assert.False(await Db.DomainUsers.AnyAsync(x => x.Id == user.Id));
    }

    [Fact]
    public async Task DeleteAsync_DeletesThePersonalSpaceEntirely_IncludingItsContent()
    {
        var user = await AddUserAsync();
        var personal = await AddSpaceAsync(user.Id, isPersonal: true);
        await AddMembershipAsync(personal.Id, user.Id, isOwner: true, DateTimeOffset.UtcNow);
        var list = new ShoppingList { Id = Guid.NewGuid(), SpaceId = personal.Id };
        Db.ShoppingLists.Add(list);
        Db.ShoppingItems.Add(new ShoppingItem { Id = Guid.NewGuid(), ShoppingListId = list.Id, RawText = "latte", NormalizedName = "latte", AddedByUserId = user.Id, AddedAt = DateTimeOffset.UtcNow });
        await Db.SaveChangesAsync();

        await deletion.DeleteAsync(user.Id, CancellationToken.None);

        Assert.False(await Db.Spaces.AnyAsync(x => x.Id == personal.Id));
        Assert.False(await Db.ShoppingLists.AnyAsync(x => x.SpaceId == personal.Id));
        Assert.False(await Db.ShoppingItems.AnyAsync(x => x.ShoppingListId == list.Id));
    }

    [Fact]
    public async Task DeleteAsync_PseudonymizesSharedSpaceMembership_KeepingContentWithTheOrphanedUserId()
    {
        var deletedUser = await AddUserAsync();
        var otherUser = await AddUserAsync();
        var shared = await AddSpaceAsync(otherUser.Id, isPersonal: false);
        await AddMembershipAsync(shared.Id, otherUser.Id, isOwner: true, DateTimeOffset.UtcNow.AddDays(-10));
        var membership = await AddMembershipAsync(shared.Id, deletedUser.Id, isOwner: false, DateTimeOffset.UtcNow.AddDays(-5));
        membership.Permissions.Add(new MembershipPermission { Resource = ResourceKind.ShoppingList, Level = AccessLevel.Write });
        var list = new ShoppingList { Id = Guid.NewGuid(), SpaceId = shared.Id };
        Db.ShoppingLists.Add(list);
        var item = new ShoppingItem { Id = Guid.NewGuid(), ShoppingListId = list.Id, RawText = "pane", NormalizedName = "pane", AddedByUserId = deletedUser.Id, AddedAt = DateTimeOffset.UtcNow };
        Db.ShoppingItems.Add(item);
        await Db.SaveChangesAsync();

        await deletion.DeleteAsync(deletedUser.Id, CancellationToken.None);

        // The space and its content survive, still attributed to the now-orphaned GUID.
        Assert.True(await Db.Spaces.AnyAsync(x => x.Id == shared.Id));
        var survivingItem = await Db.ShoppingItems.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(deletedUser.Id, survivingItem.AddedByUserId);

        // The membership itself, and its permissions, are gone.
        Assert.False(await Db.Memberships.AnyAsync(x => x.SpaceId == shared.Id && x.UserId == deletedUser.Id));
        Assert.False(await Db.Set<MembershipPermission>().AnyAsync(x => x.MembershipId == membership.Id));

        // A new archive row records the departure, with an erased (not placeholder) display name.
        var archive = await Db.MembershipArchives.SingleAsync(x => x.SpaceId == shared.Id && x.UserId == deletedUser.Id);
        Assert.Equal("", archive.DisplayNameSnapshot);
        Assert.Equal(MembershipEndReason.AccountDeleted, archive.Reason);
    }

    [Fact]
    public async Task DeleteAsync_TransfersOwnership_ToTheEarliestJoinedOtherMember()
    {
        var owner = await AddUserAsync();
        var earlier = await AddUserAsync();
        var later = await AddUserAsync();
        var shared = await AddSpaceAsync(owner.Id, isPersonal: false);
        await AddMembershipAsync(shared.Id, owner.Id, isOwner: true, DateTimeOffset.UtcNow.AddDays(-30));
        await AddMembershipAsync(shared.Id, earlier.Id, isOwner: false, DateTimeOffset.UtcNow.AddDays(-20));
        await AddMembershipAsync(shared.Id, later.Id, isOwner: false, DateTimeOffset.UtcNow.AddDays(-5));

        await deletion.DeleteAsync(owner.Id, CancellationToken.None);

        var newOwnerMembership = await Db.Memberships.SingleAsync(x => x.SpaceId == shared.Id && x.UserId == earlier.Id);
        Assert.True(newOwnerMembership.IsOwner);
        var stillMember = await Db.Memberships.SingleAsync(x => x.SpaceId == shared.Id && x.UserId == later.Id);
        Assert.False(stillMember.IsOwner);
    }

    [Fact]
    public async Task DeleteAsync_DeletesTheSharedSpace_WhenTheOwnerWasItsOnlyMember()
    {
        var owner = await AddUserAsync();
        var shared = await AddSpaceAsync(owner.Id, isPersonal: false);
        await AddMembershipAsync(shared.Id, owner.Id, isOwner: true, DateTimeOffset.UtcNow);

        await deletion.DeleteAsync(owner.Id, CancellationToken.None);

        Assert.False(await Db.Spaces.AnyAsync(x => x.Id == shared.Id));
    }

    [Fact]
    public async Task DeleteAsync_ErasesTheDisplayNameOnArchiveRowsFromEarlierDepartures()
    {
        var user = await AddUserAsync();
        Db.MembershipArchives.Add(new MembershipArchive
        {
            SpaceId = Guid.NewGuid(), UserId = user.Id, DisplayNameSnapshot = "Marco",
            JoinedAt = DateTimeOffset.UtcNow.AddYears(-1), LeftAt = DateTimeOffset.UtcNow.AddMonths(-1), Reason = MembershipEndReason.Left,
        });
        await Db.SaveChangesAsync();

        await deletion.DeleteAsync(user.Id, CancellationToken.None);

        var archive = await Db.MembershipArchives.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal("", archive.DisplayNameSnapshot);
        Assert.Equal(MembershipEndReason.Left, archive.Reason); // untouched
    }

    [Fact]
    public async Task DeleteAsync_RemovesChannelIdentitiesLinkTokensPushSubscriptionsAndConversationState()
    {
        var user = await AddUserAsync();
        Db.ChannelIdentities.Add(new ChannelIdentity { Id = Guid.NewGuid(), UserId = user.Id, ChannelName = "telegram", ExternalUserId = "123", LinkedAt = DateTimeOffset.UtcNow });
        Db.LinkTokens.Add(new LinkToken { Id = Guid.NewGuid(), Token = "abc", UserId = user.Id, ChannelName = "telegram", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        Db.PushSubscriptions.Add(new PushSubscription { Id = Guid.NewGuid(), UserId = user.Id, Endpoint = "https://push.example/1", P256dh = "key", Auth = "auth", CreatedAt = DateTimeOffset.UtcNow });
        Db.ConversationStates.Add(new ConversationState { Id = Guid.NewGuid(), UserId = user.Id, UpdatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
        await Db.SaveChangesAsync();

        await deletion.DeleteAsync(user.Id, CancellationToken.None);

        Assert.False(await Db.ChannelIdentities.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await Db.LinkTokens.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await Db.PushSubscriptions.AnyAsync(x => x.UserId == user.Id));
        Assert.False(await Db.ConversationStates.AnyAsync(x => x.UserId == user.Id));
    }

    [Fact]
    public async Task DeleteAsync_ResetsDefaultSpaceId_OnOtherUsersPointingAtTheDeletedPersonalSpace()
    {
        var owner = await AddUserAsync();
        var personal = await AddSpaceAsync(owner.Id, isPersonal: true);
        await AddMembershipAsync(personal.Id, owner.Id, isOwner: true, DateTimeOffset.UtcNow);
        // Implausible in real usage (a personal space belongs to one person), but proves
        // DeleteSpaceAsync's own defensive sweep rather than assuming it from the outside.
        var otherUser = await AddUserAsync(defaultSpaceId: personal.Id);

        await deletion.DeleteAsync(owner.Id, CancellationToken.None);

        var reloaded = await Db.DomainUsers.AsNoTracking().SingleAsync(x => x.Id == otherUser.Id);
        Assert.Null(reloaded.DefaultSpaceId);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAnyPendingLastOperation_ForTheDeletedUser()
    {
        var user = await AddUserAsync();
        Db.LastOperations.Add(new LastOperation
        {
            UserId = user.Id, SpaceId = Guid.NewGuid(), OperationType = "shopping.add",
            UndoPayloadJson = "{}", PerformedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await Db.SaveChangesAsync();

        await deletion.DeleteAsync(user.Id, CancellationToken.None);

        Assert.False(await Db.LastOperations.AnyAsync(x => x.UserId == user.Id));
    }
}
