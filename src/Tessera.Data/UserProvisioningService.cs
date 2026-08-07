using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

public sealed class UserProvisioningService(TesseraDbContext db)
{
    public async Task ProvisionAsync(Guid userId, string email, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var space = new Space
        {
            Id = Guid.NewGuid(),
            Name = "Personale",
            OwnerId = userId,
            CreatedAt = now,
            PlanId = SystemPlanIds.Free,
            IsPersonal = true,
        };
        var user = new DomainUser
        {
            Id = userId,
            Email = email,
            CreatedAt = now,
            DefaultSpaceId = space.Id,
        };
        var membership = new Membership
        {
            Id = Guid.NewGuid(),
            SpaceId = space.Id,
            UserId = userId,
            IsOwner = true,
            JoinedAt = now,
        };

        db.DomainUsers.Add(user);
        db.Spaces.Add(space);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
    }

    // Behind /language (docs/09-localizzazione.md) — the fix for whoever got the wrong
    // default and would otherwise have to find the console to correct it.
    public async Task SetPreferredCultureAsync(Guid userId, string culture, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(x => x.Id == userId, ct);
        user.PreferredCulture = culture;
        await db.SaveChangesAsync(ct);
    }

    // IANA id, independent of PreferredCulture (docs/09-localizzazione.md: "un italiano a
    // Londra vuole l'interfaccia in italiano e gli orari in Europe/London").
    public async Task SetTimeZoneAsync(Guid userId, string timeZoneId, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(x => x.Id == userId, ct);
        user.TimeZoneId = timeZoneId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<DomainUser> GetAsync(Guid userId, CancellationToken ct) =>
        await db.DomainUsers.AsNoTracking().FirstAsync(x => x.Id == userId, ct);

    public async Task SetDefaultSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        var isMember = await db.Memberships.AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId, ct);
        if (!isMember)
        {
            throw new InvalidOperationException($"User {userId} is not a member of space {spaceId}.");
        }

        var user = await db.DomainUsers.FirstAsync(x => x.Id == userId, ct);
        user.DefaultSpaceId = spaceId;
        await db.SaveChangesAsync(ct);
    }
}
