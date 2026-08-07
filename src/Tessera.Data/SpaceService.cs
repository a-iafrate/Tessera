using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// "Admin per spazio" (docs/06-roadmap.md) is Membership.IsOwner — the docs' own Membership
// model has no separate admin flag, and there's exactly one owner/admin tier per space.
public sealed class SpaceService(TesseraDbContext db)
{
    public async Task<Space> CreateAsync(Guid ownerUserId, string name, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var space = new Space
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerUserId,
            CreatedAt = now,
            PlanId = SystemPlanIds.Free,
            IsPersonal = false,
        };
        db.Spaces.Add(space);

        db.Memberships.Add(new Membership
        {
            Id = Guid.NewGuid(),
            SpaceId = space.Id,
            UserId = ownerUserId,
            IsOwner = true,
            JoinedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return space;
    }

    public async Task<IReadOnlyList<Space>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.Memberships
            .Where(m => m.UserId == userId)
            .Join(db.Spaces, m => m.SpaceId, s => s.Id, (m, s) => s)
            .OrderBy(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<Space?> GetByIdAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var isMember = await db.Memberships.AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct);
        return isMember ? await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId, ct) : null;
    }

    public async Task<bool> IsOwnerAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var membership = await db.Memberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct);
        return membership?.IsOwner ?? false;
    }

    public async Task RenameAsync(Guid spaceId, Guid userId, string newName, CancellationToken ct)
    {
        if (!await IsOwnerAsync(spaceId, userId, ct))
        {
            throw new UnauthorizedAccessException($"User {userId} is not the Admin of space {spaceId}.");
        }

        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        space.Name = newName;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(Membership Membership, string DisplayName)>> GetMembersAsync(
        Guid spaceId, Guid userId, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct))
        {
            return [];
        }

        var members = await db.Memberships
            .Where(m => m.SpaceId == spaceId)
            .Include(m => m.Permissions)
            .Join(db.DomainUsers, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .AsNoTracking()
            .ToListAsync(ct);

        return members
            .OrderByDescending(x => x.m.IsOwner)
            .ThenBy(x => x.m.JoinedAt)
            .Select(x => (x.m, x.u.DisplayName ?? x.u.Email))
            .ToList();
    }

    public async Task<IReadOnlyList<MembershipArchive>> GetFormerMembersAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        if (!await db.Memberships.AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct))
        {
            return [];
        }

        return await db.MembershipArchives
            .Where(a => a.SpaceId == spaceId)
            .OrderByDescending(a => a.LeftAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task TransferOwnershipAsync(Guid spaceId, Guid currentOwnerUserId, Guid newOwnerUserId, CancellationToken ct)
    {
        var currentOwnerMembership = await db.Memberships
            .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == currentOwnerUserId, ct);
        if (currentOwnerMembership is null || !currentOwnerMembership.IsOwner)
        {
            throw new UnauthorizedAccessException($"User {currentOwnerUserId} is not the Admin of space {spaceId}.");
        }

        var newOwnerMembership = await db.Memberships.FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == newOwnerUserId, ct)
            ?? throw new InvalidOperationException($"User {newOwnerUserId} is not a member of space {spaceId}.");

        currentOwnerMembership.IsOwner = false;
        newOwnerMembership.IsOwner = true;
        await db.SaveChangesAsync(ct);
    }

    // Only the Admin can remove someone else — docs/02-modello-dati.md's "same data rules as
    // leaving, with a communication difference": the removed person must be told separately
    // (the caller's job, not this method's), or they'd just find the bot gone quiet.
    public async Task RemoveMemberAsync(Guid spaceId, Guid actingUserId, Guid targetUserId, CancellationToken ct)
    {
        if (actingUserId == targetUserId)
        {
            throw new InvalidOperationException("Use LeaveAsync to remove yourself.");
        }

        if (!await IsOwnerAsync(spaceId, actingUserId, ct))
        {
            throw new UnauthorizedAccessException($"User {actingUserId} is not the Admin of space {spaceId}.");
        }

        await RemoveMembershipAsync(spaceId, targetUserId, MembershipEndReason.Removed, ct);
    }

    // The member's own departure. Enforces the last-Admin rules (docs/02-modello-dati.md):
    // the personal space can never be left, a sole Owner leaving deletes the whole space
    // (nobody is left to own its data), and an Owner with other members must transfer the
    // role first rather than leaving the space without one.
    public async Task LeaveAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        if (space.IsPersonal)
        {
            throw new InvalidOperationException("The personal space can't be left, only deleted with the account.");
        }

        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct)
            ?? throw new InvalidOperationException($"User {userId} is not a member of space {spaceId}.");

        if (membership.IsOwner)
        {
            var otherMembersCount = await db.Memberships.CountAsync(m => m.SpaceId == spaceId && m.UserId != userId, ct);
            if (otherMembersCount > 0)
            {
                throw new InvalidOperationException("Transfer the Admin role to another member before leaving.");
            }

            await DeleteSpaceAsync(spaceId, ct);
            return;
        }

        await RemoveMembershipAsync(spaceId, userId, MembershipEndReason.Left, ct);
    }

    private async Task RemoveMembershipAsync(Guid spaceId, Guid userId, MembershipEndReason reason, CancellationToken ct)
    {
        var membership = await db.Memberships
            .Include(m => m.Permissions)
            .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct);
        if (membership is null)
        {
            return;
        }

        var user = await db.DomainUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);

        // Items/expenses/reminders this member created (AddedByUserId etc.) are deliberately
        // left untouched — they have no FK to User (hard rule 3) and survive as-is, resolved
        // later via ActorNameResolver + this archive row.
        db.MembershipArchives.Add(new MembershipArchive
        {
            SpaceId = spaceId,
            UserId = userId,
            DisplayNameSnapshot = user?.DisplayName ?? user?.Email ?? "?",
            JoinedAt = membership.JoinedAt,
            LeftAt = DateTimeOffset.UtcNow,
            Reason = reason,
        });

        db.MembershipPermissions.RemoveRange(membership.Permissions);
        db.Memberships.Remove(membership);

        if (user is not null && user.DefaultSpaceId == spaceId)
        {
            user.DefaultSpaceId = null;
        }

        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is not null && state.ActiveSpaceId == spaceId)
        {
            state.ActiveSpaceId = null;
        }

        await db.SaveChangesAsync(ct);
    }

    // The sole member leaving is equivalent to deleting the space (docs/02-modello-dati.md):
    // there's nobody left for its data to belong to. Every SpaceId-tagged table is cleaned up
    // explicitly — none of them carry a real FK to Space (by the same design as the
    // AddedByUserId/SpaceId pattern elsewhere), so nothing would cascade automatically.
    private async Task DeleteSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);

        var membership = await db.Memberships.Include(m => m.Permissions).FirstOrDefaultAsync(m => m.SpaceId == spaceId, ct);
        if (membership is not null)
        {
            db.MembershipPermissions.RemoveRange(membership.Permissions);
            db.Memberships.Remove(membership);
        }

        var shoppingLists = await db.ShoppingLists.Where(l => l.SpaceId == spaceId).ToListAsync(ct);
        foreach (var list in shoppingLists)
        {
            db.ShoppingItems.RemoveRange(await db.ShoppingItems.Where(i => i.ShoppingListId == list.Id).ToListAsync(ct));
        }
        db.ShoppingLists.RemoveRange(shoppingLists);

        db.Expenses.RemoveRange(await db.Expenses.Where(e => e.SpaceId == spaceId).ToListAsync(ct));
        db.MerchantCategoryMappings.RemoveRange(await db.MerchantCategoryMappings.Where(m => m.SpaceId == spaceId).ToListAsync(ct));
        db.PendingExpenseConfirmations.RemoveRange(await db.PendingExpenseConfirmations.Where(p => p.SpaceId == spaceId).ToListAsync(ct));
        db.Reminders.RemoveRange(await db.Reminders.Where(r => r.SpaceId == spaceId).ToListAsync(ct));
        db.RecurringExpenses.RemoveRange(await db.RecurringExpenses.Where(r => r.SpaceId == spaceId).ToListAsync(ct));
        db.Budgets.RemoveRange(await db.Budgets.Where(b => b.SpaceId == spaceId).ToListAsync(ct));
        db.InviteTokens.RemoveRange(await db.InviteTokens.Where(i => i.SpaceId == spaceId).ToListAsync(ct));
        db.Categories.RemoveRange(await db.Categories.Where(c => c.SpaceId == spaceId).ToListAsync(ct));
        db.MembershipArchives.RemoveRange(await db.MembershipArchives.Where(a => a.SpaceId == spaceId).ToListAsync(ct));

        var usersWithDefaultHere = await db.DomainUsers.Where(u => u.DefaultSpaceId == spaceId).ToListAsync(ct);
        foreach (var user in usersWithDefaultHere)
        {
            user.DefaultSpaceId = null;
        }

        var statesPointingHere = await db.ConversationStates.Where(s => s.ActiveSpaceId == spaceId).ToListAsync(ct);
        foreach (var state in statesPointingHere)
        {
            state.ActiveSpaceId = null;
        }

        db.Spaces.Remove(space);
        await db.SaveChangesAsync(ct);
    }
}
