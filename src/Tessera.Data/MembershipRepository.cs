using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;
using Tessera.Data.Caching;

namespace Tessera.Data;

// Resolved on every message and every console permission check — the busiest read in the
// authorization path (docs/05-ottimizzazioni.md, "Cache: cosa e per quanto" — 5 min TTL). A
// stale hit here means a permission change takes up to 5 minutes to take effect, which the
// docs call acceptable *only* because mutators invalidate explicitly instead of waiting out
// the TTL: SpaceService and InviteService call Invalidate after any write to a Membership or
// its MembershipPermissions.
public sealed class MembershipRepository(TesseraDbContext db, IMemoryCache cache) : IMembershipRepository
{
    public async Task<Membership?> FindAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKeys.Membership(userId, spaceId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl.MembershipPermissions;
            return await db.Memberships
                .Include(x => x.Permissions)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId && x.SpaceId == spaceId, ct);
        });

    // Static because callers that mutate membership (SpaceService, InviteService) go through
    // TesseraDbContext directly, not through this repository — there's no instance to call
    // Invalidate on at the point where the write happens. Takes the IMemoryCache explicitly
    // for the same reason, rather than assuming a shared instance.
    public static void Invalidate(IMemoryCache cache, Guid userId, Guid spaceId) =>
        cache.Remove(CacheKeys.Membership(userId, spaceId));
}
