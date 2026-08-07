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
}
