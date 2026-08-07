using Microsoft.EntityFrameworkCore;

namespace Tessera.Data;

// The one place actor names are resolved (hard rule 3, docs/02-modello-dati.md) — never a
// direct join on User, since AddedByUserId/CreatedByUserId/CheckedByUserId carry no FK and
// the referenced account may no longer exist. Returns null when nothing resolves; the
// caller (which has the localizer, unlike this Data-layer class) applies the
// "Space.FormerMember" fallback text.
public sealed class ActorNameResolver(TesseraDbContext db)
{
    public async Task<string?> ResolveAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var isActiveMember = await db.Memberships.AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct);
        if (isActiveMember)
        {
            var user = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is not null)
            {
                return user.DisplayName ?? user.Email;
            }
        }

        var archived = await db.MembershipArchives
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SpaceId == spaceId && a.UserId == userId, ct);
        return archived?.DisplayNameSnapshot;
    }
}
