using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;
using DomainUser = Tessera.Core.Users.User;

namespace Tessera.Data;

public sealed class UserProvisioningService(TesseraDbContext db)
{
    // timeZoneId can be null only because not every signup surface can collect it yet
    // (social login has no form of its own) — left unset, every reminder/digest/calendar
    // computation silently falls back to UTC until the person finds Profile and sets it
    // (the bug this parameter exists to prevent). Register.razor always passes one, detected
    // from the browser (docs/09-localizzazione.md).
    //
    // personalSpaceName is resolved by the caller, not here: Tessera.Data stays free of
    // IStringLocalizer (same convention as OnboardingService's hint keys) — the caller already
    // has one, and at signup time there's no User.PreferredCulture yet, only the ambient
    // CurrentUICulture the request-localization middleware set from Accept-Language.
    public async Task ProvisionAsync(
        Guid userId, string email, string? timeZoneId, string personalSpaceName, CancellationToken ct,
        string? displayName = null, string? pictureUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        var space = new Space
        {
            Id = Guid.NewGuid(),
            Name = personalSpaceName,
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
            TimeZoneId = timeZoneId,
            DisplayName = displayName,
            PictureUrl = pictureUrl,
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

    // Set on Profile, independent of how the account signed in — a value here always wins
    // over whatever a future Google sign-in claim would otherwise suggest, since provisioning
    // only ever writes DisplayName/PictureUrl once, at account creation (see ExternalLogin.razor).
    public async Task SetDisplayNameAsync(Guid userId, string? displayName, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(x => x.Id == userId, ct);
        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPictureUrlAsync(Guid userId, string? pictureUrl, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstAsync(x => x.Id == userId, ct);
        user.PictureUrl = string.IsNullOrWhiteSpace(pictureUrl) ? null : pictureUrl.Trim();
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
