using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using ChannelIdentity = Tessera.Core.Users.ChannelIdentity;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

public sealed class ChannelIdentityRepository(TesseraDbContext db) : IChannelIdentityRepository
{
    public async Task<DomainUser?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct)
    {
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
    }

    public async Task<IReadOnlyList<ChannelIdentity>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.ChannelIdentities
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
}
