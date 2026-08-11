using Microsoft.EntityFrameworkCore;
using Tessera.Core.Calendars;

namespace Tessera.Data;

// A calendar's owner decides which of their own spaces can see it, and at what level — this
// is deliberately separate from LinkedAccountService (account/OAuth concerns) since mapping is
// a per-space, per-calendar decision, not an account one (docs/02-modello-dati.md).
public sealed class CalendarSpaceService(TesseraDbContext db)
{
    public async Task<IReadOnlyList<ExternalCalendar>> GetMyCalendarsAsync(Guid userId, CancellationToken ct)
    {
        var accountIds = await db.LinkedAccounts.Where(x => x.UserId == userId).Select(x => x.Id).ToListAsync(ct);
        return await db.ExternalCalendars
            .Where(x => accountIds.Contains(x.LinkedAccountId) && x.IsEnabled)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Dictionary<Guid, CalendarSpaceMapping>> GetMappingsAsync(
        Guid spaceId, IReadOnlyList<Guid> calendarIds, CancellationToken ct) =>
        await db.CalendarSpaceMappings
            .Where(x => x.SpaceId == spaceId && calendarIds.Contains(x.ExternalCalendarId))
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ExternalCalendarId, ct);

    // shareLevel: null removes the mapping (the calendar goes back to "not exposed to this
    // space"). Only the calendar's own owner may change its mapping — exposing someone else's
    // personal calendar into a space isn't theirs to decide.
    public async Task SetMappingAsync(
        Guid userId, Guid spaceId, Guid externalCalendarId, CalendarShareLevel? shareLevel, bool isDefaultWriteTarget, CancellationToken ct)
    {
        var ownsCalendar = await db.ExternalCalendars
            .Join(db.LinkedAccounts, c => c.LinkedAccountId, a => a.Id, (c, a) => new { Calendar = c, a.UserId })
            .AnyAsync(x => x.Calendar.Id == externalCalendarId && x.UserId == userId, ct);
        if (!ownsCalendar)
        {
            throw new UnauthorizedAccessException($"User {userId} does not own calendar {externalCalendarId}.");
        }

        var mapping = await db.CalendarSpaceMappings
            .FirstOrDefaultAsync(x => x.ExternalCalendarId == externalCalendarId && x.SpaceId == spaceId, ct);

        if (shareLevel is null)
        {
            if (mapping is not null)
            {
                db.CalendarSpaceMappings.Remove(mapping);
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        if (mapping is null)
        {
            mapping = new CalendarSpaceMapping { ExternalCalendarId = externalCalendarId, SpaceId = spaceId };
            db.CalendarSpaceMappings.Add(mapping);
        }

        mapping.ShareLevel = shareLevel.Value;
        mapping.IsDefaultWriteTarget = isDefaultWriteTarget && shareLevel == CalendarShareLevel.Write;

        // Exactly one default write target per space — a second "create events here" answer
        // would leave "where do I create the event?" as ambiguous as having none
        // (docs/02-modello-dati.md).
        if (mapping.IsDefaultWriteTarget)
        {
            var others = await db.CalendarSpaceMappings
                .Where(x => x.SpaceId == spaceId && x.ExternalCalendarId != externalCalendarId && x.IsDefaultWriteTarget)
                .ToListAsync(ct);
            foreach (var other in others)
            {
                other.IsDefaultWriteTarget = false;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
