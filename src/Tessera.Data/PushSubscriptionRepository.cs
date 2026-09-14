using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Users;

namespace Tessera.Data;

public sealed class PushSubscriptionRepository(TesseraDbContext db) : IPushSubscriptionRepository
{
    public async Task<IReadOnlyList<PushSubscription>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.PushSubscriptions
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);

    public async Task AddAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken ct)
    {
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        if (existing is not null)
        {
            existing.UserId = userId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            await db.SaveChangesAsync(ct);
            return;
        }

        db.PushSubscriptions.Add(new PushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string endpoint, CancellationToken ct)
    {
        await db.PushSubscriptions.Where(x => x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
    }
}
