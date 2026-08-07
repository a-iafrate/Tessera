using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// Mirrors LinkService's token pattern (docs/02-modello-dati.md) — no email sender exists
// yet (docs/07-compliance.md), so the token itself, shared out-of-band, is the invite.
public sealed class InviteService(TesseraDbContext db)
{
    public async Task<InviteToken> CreateAsync(
        Guid spaceId, Guid invitedByUserId, AccessLevel shoppingListLevel, AccessLevel expensesLevel,
        AccessLevel remindersLevel, AccessLevel calendarLevel, CancellationToken ct)
    {
        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == invitedByUserId, ct);
        if (membership is null || !membership.IsOwner)
        {
            throw new UnauthorizedAccessException($"User {invitedByUserId} is not the Admin of space {spaceId}.");
        }

        var invite = new InviteToken
        {
            Id = Guid.NewGuid(),
            Token = GenerateToken(),
            SpaceId = spaceId,
            InvitedByUserId = invitedByUserId,
            ShoppingListLevel = shoppingListLevel,
            ExpensesLevel = expensesLevel,
            RemindersLevel = remindersLevel,
            CalendarLevel = calendarLevel,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.InviteTokens.Add(invite);
        await db.SaveChangesAsync(ct);
        return invite;
    }

    // Null covers "not found", "already consumed" and "expired" alike — the accept page
    // shows one generic "invalid or expired" state either way.
    public async Task<InviteToken?> FindValidAsync(string token, CancellationToken ct)
    {
        var invite = await db.InviteTokens.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token, ct);
        return IsValid(invite) ? invite : null;
    }

    // What the accept page shows before the user commits — space and inviter name, not the
    // raw permission levels.
    public async Task<(string SpaceName, string InviterDisplayName)?> GetPreviewAsync(string token, CancellationToken ct)
    {
        var invite = await FindValidAsync(token, ct);
        if (invite is null)
        {
            return null;
        }

        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == invite.SpaceId, ct);
        var inviter = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == invite.InvitedByUserId, ct);
        if (space is null || inviter is null)
        {
            return null;
        }

        return (space.Name, inviter.DisplayName ?? inviter.Email);
    }

    public async Task<Membership?> ConsumeAsync(string token, Guid userId, CancellationToken ct)
    {
        var invite = await db.InviteTokens.FirstOrDefaultAsync(x => x.Token == token, ct);
        if (!IsValid(invite))
        {
            return null;
        }

        var alreadyMember = await db.Memberships.AnyAsync(m => m.SpaceId == invite!.SpaceId && m.UserId == userId, ct);
        if (alreadyMember)
        {
            invite!.ConsumedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return null;
        }

        var membership = new Membership
        {
            Id = Guid.NewGuid(),
            SpaceId = invite!.SpaceId,
            UserId = userId,
            IsOwner = false,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        db.Memberships.Add(membership);

        AccessLevel[] levels = [invite.ShoppingListLevel, invite.ExpensesLevel, invite.RemindersLevel, invite.CalendarLevel];
        ResourceKind[] resources = [ResourceKind.ShoppingList, ResourceKind.Expenses, ResourceKind.Reminders, ResourceKind.Calendar];
        for (var i = 0; i < resources.Length; i++)
        {
            if (levels[i] != AccessLevel.None)
            {
                db.MembershipPermissions.Add(new MembershipPermission
                {
                    MembershipId = membership.Id,
                    Resource = resources[i],
                    Level = levels[i],
                });
            }
        }

        invite.ConsumedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return membership;
    }

    private static bool IsValid(InviteToken? invite) =>
        invite is not null && invite.ConsumedAt is null && invite.ExpiresAt >= DateTimeOffset.UtcNow;

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
