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
}
