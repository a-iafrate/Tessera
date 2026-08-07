using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Tessera.Data;

// Right of access + right to erasure (docs/07-compliance.md, docs/02-modello-dati.md §Caso 2).
// Erasure here is pseudonymization, not cascading deletion: content in shared spaces stays so
// other members' history and totals remain intact, but every identifying field on this account
// is stripped — the orphaned GUID left on AddedByUserId/CreatedByUserId is no longer personal
// data once nothing links it back to a person. Only the "Personale" space, which by
// construction nobody else is a member of, is deleted outright along with its content.
public sealed class AccountDeletionService(TesseraDbContext db, SpaceService spaces)
{
    public async Task<string> ExportAsJsonAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.DomainUsers.AsNoTracking().FirstAsync(u => u.Id == userId, ct);

        var memberSpaces = await db.Memberships
            .Where(m => m.UserId == userId)
            .Join(db.Spaces, m => m.SpaceId, s => s.Id, (m, s) => new { m.IsOwner, m.JoinedAt, s.Name, s.IsPersonal })
            .AsNoTracking()
            .ToListAsync(ct);

        var channelIdentities = await db.ChannelIdentities
            .Where(c => c.UserId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        var shoppingItems = await db.ShoppingItems
            .Where(i => i.AddedByUserId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        var expenses = await db.Expenses
            .Where(e => e.CreatedByUserId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        var reminders = await db.Reminders
            .Where(r => r.CreatedByUserId == userId)
            .AsNoTracking()
            .ToListAsync(ct);

        var export = new
        {
            Profile = new
            {
                user.Email,
                user.DisplayName,
                user.PreferredCulture,
                user.TimeZoneId,
                user.CreatedAt,
            },
            Spaces = memberSpaces.Select(x => new
            {
                x.Name,
                x.IsPersonal,
                Role = x.IsOwner ? "Admin" : "Member",
                x.JoinedAt,
            }),
            ChannelIdentities = channelIdentities.Select(c => new { c.ChannelName, c.LinkedAt }),
            ShoppingItemsAdded = shoppingItems.Select(i => new { i.RawText, i.AddedAt, i.IsChecked }),
            ExpensesRecorded = expenses.Select(e => new { e.Amount, e.Currency, e.Merchant, e.Date, e.Note }),
            RemindersCreated = reminders.Select(r => new { r.Text, r.DueAt, r.IsCompleted }),
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task DeleteAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.DomainUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return;
        }

        var memberships = await db.Memberships.Where(m => m.UserId == userId).ToListAsync(ct);

        foreach (var membership in memberships)
        {
            var space = await db.Spaces.FirstAsync(s => s.Id == membership.SpaceId, ct);

            if (space.IsPersonal)
            {
                await spaces.DeleteSpaceAsync(space.Id, ct);
                continue;
            }

            if (membership.IsOwner)
            {
                // The last-Admin rule (docs/02-modello-dati.md) can't require a manual
                // transfer here — the account is leaving unconditionally, not asking
                // permission. The earliest-joined other member inherits the role instead.
                var successor = await db.Memberships
                    .Where(m => m.SpaceId == space.Id && m.UserId != userId)
                    .OrderBy(m => m.JoinedAt)
                    .FirstOrDefaultAsync(ct);

                if (successor is null)
                {
                    await spaces.DeleteSpaceAsync(space.Id, ct);
                    continue;
                }

                successor.IsOwner = true;
                await db.SaveChangesAsync(ct);
            }

            await spaces.PseudonymizeMembershipAsync(space.Id, userId, ct);
        }

        // Earlier Leave/Remove events already archived under this UserId keep their dates,
        // but the name they displayed must be erased too — erasure isn't limited to the
        // memberships active right now.
        var priorArchives = await db.MembershipArchives.Where(a => a.UserId == userId).ToListAsync(ct);
        foreach (var archive in priorArchives)
        {
            archive.DisplayNameSnapshot = "";
        }

        db.ChannelIdentities.RemoveRange(await db.ChannelIdentities.Where(c => c.UserId == userId).ToListAsync(ct));
        db.LinkTokens.RemoveRange(await db.LinkTokens.Where(l => l.UserId == userId).ToListAsync(ct));

        var conversationState = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (conversationState is not null)
        {
            db.ConversationStates.Remove(conversationState);
        }

        db.DomainUsers.Remove(user);
        await db.SaveChangesAsync(ct);
    }
}
