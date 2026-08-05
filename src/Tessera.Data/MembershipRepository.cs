using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class MembershipRepository(TesseraDbContext db) : IMembershipRepository
{
    public async Task<Membership?> FindAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        await db.Memberships
            .Include(x => x.Permissions)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.SpaceId == spaceId, ct);
}
