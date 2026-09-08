using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tessera.Core.Abstractions;
using Tessera.Data.Caching;
using ChannelIdentity = Tessera.Core.Users.ChannelIdentity;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

// Read on every inbound message to resolve who's writing (docs/05-ottimizzazioni.md, "Cache:
// cosa e per quanto" — 15 min TTL, "cambia raramente"). No explicit invalidation on top of the
// TTL: a channel identity is only created once at linking time and never mutated afterward, so
// there's no write path that would leave the cache stale.
public sealed class ChannelIdentityRepository(TesseraDbContext db, IMemoryCache cache) : IChannelIdentityRepository
{
    public async Task<DomainUser?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync(CacheKeys.ChannelIdentity(channelName, externalUserId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl.ChannelIdentity;

            var identity = await db.ChannelIdentities
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ChannelName == channelName && x.ExternalUserId == externalUserId, ct);
            if (identity is null)
            {
                return null;
            }

            return await db.DomainUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == identity.UserId, ct);
        });
    }

    public async Task<IReadOnlyList<ChannelIdentity>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.ChannelIdentities
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
}
