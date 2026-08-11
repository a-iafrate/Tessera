using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Spaces;
using Tessera.Core.Users;

namespace Tessera.Data;

// The space-facing read/write surface over every calendar mapped into a space — merges
// multiple members' calendars into one view, computing the effective level per calendar the
// same way everywhere (EffectiveCalendarLevel, hard rule 15) before deciding what it may
// contribute to that view.
public sealed class CalendarQueryService(
    TesseraDbContext db, IMembershipRepository memberships, ICalendarProvider calendarProvider, LinkedAccountService linkedAccounts,
    ILogger<CalendarQueryService> logger)
{
    public async Task<IReadOnlyList<CalendarEventInfo>> GetEventsAsync(
        Guid spaceId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var membershipLevel = await GetMembershipCalendarLevelAsync(userId, spaceId, ct);
        if (membershipLevel < AccessLevel.Read)
        {
            return [];
        }

        var candidates = await GetAccessibleCalendarsAsync(spaceId, membershipLevel, AccessLevel.Read, ct);
        var events = new List<CalendarEventInfo>();
        foreach (var (calendar, account) in candidates)
        {
            var accessToken = await linkedAccounts.GetValidAccessTokenAsync(account, ct);
            events.AddRange(await calendarProvider.GetEventsAsync(accessToken, calendar.ProviderCalendarId, from, to, ct));
        }

        // The same event reachable through two members' linked copies of a shared calendar
        // must show up once, not twice (docs/02-modello-dati.md, docs/03-integrazioni.md).
        return events
            .GroupBy(e => e.IcalUid ?? e.ProviderEventId)
            .Select(g => g.First())
            .OrderBy(e => e.Start)
            .ToList();
    }

    public async Task<IReadOnlyList<FreeBusyInterval>> GetFreeBusyAsync(
        Guid spaceId, Guid userId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var membershipLevel = await GetMembershipCalendarLevelAsync(userId, spaceId, ct);
        if (membershipLevel < AccessLevel.Availability)
        {
            return [];
        }

        var candidates = await GetAccessibleCalendarsAsync(spaceId, membershipLevel, AccessLevel.Availability, ct);
        var intervals = new List<FreeBusyInterval>();
        foreach (var group in candidates.GroupBy(x => x.Account.Id))
        {
            var accessToken = await linkedAccounts.GetValidAccessTokenAsync(group.First().Account, ct);
            var providerCalendarIds = group.Select(x => x.Calendar.ProviderCalendarId).ToList();
            intervals.AddRange(await calendarProvider.GetFreeBusyAsync(accessToken, providerCalendarIds, from, to, ct));
        }

        return MergeIntervals(intervals);
    }

    // Null covers every "can't create it" case the same way (no membership Write, no default
    // write-target mapped, provider now refuses writes) — the caller always has a single
    // graduated-failure message to fall back to regardless of which one it was
    // (docs/10-conversazione.md).
    public async Task<CalendarEventInfo?> CreateEventAsync(
        Guid spaceId, Guid userId, string title, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var membershipLevel = await GetMembershipCalendarLevelAsync(userId, spaceId, ct);
        if (membershipLevel < AccessLevel.Write)
        {
            logger.LogWarning(
                "CreateEventAsync refused: user {UserId} has membership Calendar level {Level} (< Write) in space {SpaceId}",
                userId, membershipLevel, spaceId);
            return null;
        }

        var mapping = await ResolveWriteTargetAsync(spaceId, ct);
        if (mapping is null)
        {
            var allMappings = await db.CalendarSpaceMappings.Where(x => x.SpaceId == spaceId).ToListAsync(ct);
            logger.LogWarning(
                "CreateEventAsync refused: no resolvable write target in space {SpaceId} — {Count} mapping(s), levels [{Levels}]",
                spaceId, allMappings.Count, string.Join(", ", allMappings.Select(x => $"{x.ExternalCalendarId}:{x.ShareLevel}:default={x.IsDefaultWriteTarget}")));
            return null;
        }

        var calendar = await db.ExternalCalendars.FirstOrDefaultAsync(x => x.Id == mapping.ExternalCalendarId, ct);
        var account = calendar is null ? null : await db.LinkedAccounts.FirstOrDefaultAsync(x => x.Id == calendar.LinkedAccountId, ct);
        if (calendar is null || account is null)
        {
            logger.LogWarning(
                "CreateEventAsync refused: calendar {CalendarId} or its linked account missing (calendar found={CalendarFound}, account found={AccountFound})",
                mapping.ExternalCalendarId, calendar is not null, account is not null);
            return null;
        }

        var effective = EffectiveCalendarLevel.Compute(calendar.ProviderRole, mapping.ShareLevel, membershipLevel);
        if (effective < AccessLevel.Write)
        {
            logger.LogWarning(
                "CreateEventAsync refused: effective level {Effective} (< Write) — providerRole={ProviderRole}, shareLevel={ShareLevel}, membershipLevel={MembershipLevel}",
                effective, calendar.ProviderRole, mapping.ShareLevel, membershipLevel);
            return null;
        }

        logger.LogInformation(
            "CreateEventAsync: sending start={Start} end={End} (offset {Offset}) to provider calendar {ProviderCalendarId}",
            start.ToString("O"), end.ToString("O"), start.Offset, calendar.ProviderCalendarId);

        var accessToken = await linkedAccounts.GetValidAccessTokenAsync(account, ct);
        var created = await calendarProvider.CreateEventAsync(accessToken, calendar.ProviderCalendarId, new CalendarEventDraft(title, start, end, Description: null), ct);

        logger.LogInformation("CreateEventAsync: provider returned start={Start} end={End}", created.Start.ToString("O"), created.End.ToString("O"));
        return created;
    }

    // IsDefaultWriteTarget only exists to break a tie between *multiple* writable calendars
    // (docs/02-modello-dati.md) — requiring the user to also flag the obvious, only candidate
    // as "default" is friction with no ambiguity to resolve, so a single Write-level mapping
    // is used automatically.
    private async Task<CalendarSpaceMapping?> ResolveWriteTargetAsync(Guid spaceId, CancellationToken ct)
    {
        var writable = await db.CalendarSpaceMappings
            .Where(x => x.SpaceId == spaceId && x.ShareLevel == CalendarShareLevel.Write)
            .ToListAsync(ct);

        return writable.FirstOrDefault(x => x.IsDefaultWriteTarget)
            ?? (writable.Count == 1 ? writable[0] : null);
    }

    private async Task<List<(ExternalCalendar Calendar, LinkedAccount Account)>> GetAccessibleCalendarsAsync(
        Guid spaceId, AccessLevel membershipLevel, AccessLevel minimumEffective, CancellationToken ct)
    {
        var mappings = await db.CalendarSpaceMappings.Where(x => x.SpaceId == spaceId).ToListAsync(ct);
        if (mappings.Count == 0)
        {
            return [];
        }

        var calendarIds = mappings.Select(x => x.ExternalCalendarId).ToList();
        var calendars = await db.ExternalCalendars.Where(x => calendarIds.Contains(x.Id) && x.IsEnabled).ToListAsync(ct);
        var accountIds = calendars.Select(x => x.LinkedAccountId).Distinct().ToList();
        var accountsById = await db.LinkedAccounts.Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var result = new List<(ExternalCalendar, LinkedAccount)>();
        foreach (var calendar in calendars)
        {
            var mapping = mappings.First(x => x.ExternalCalendarId == calendar.Id);
            var effective = EffectiveCalendarLevel.Compute(calendar.ProviderRole, mapping.ShareLevel, membershipLevel);
            if (effective >= minimumEffective && accountsById.TryGetValue(calendar.LinkedAccountId, out var account))
            {
                result.Add((calendar, account));
            }
        }

        return result;
    }

    // Same lookup AccessPolicy.CanAsync does internally, but the actual granted level is
    // needed here (for the min() in EffectiveCalendarLevel), not just a yes/no at one required
    // level, so IAccessPolicy's boolean CanAsync doesn't fit.
    private async Task<AccessLevel> GetMembershipCalendarLevelAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        var membership = await memberships.FindAsync(userId, spaceId, ct);
        if (membership is null)
        {
            return AccessLevel.None;
        }

        if (membership.IsOwner)
        {
            return AccessLevel.Write;
        }

        return membership.Permissions.FirstOrDefault(p => p.Resource == ResourceKind.Calendar)?.Level ?? AccessLevel.None;
    }

    // CalendarReminderJob's idempotency guard — there's no per-event DB row to hang a
    // NotifiedAt on (docs/02-modello-dati.md), so this is keyed on the event's own identity
    // plus its current start; a reschedule to a new start is treated as unnotified again.
    public Task<bool> WasNotifiedAsync(Guid spaceId, Guid userId, string eventKey, DateTimeOffset eventStart, CancellationToken ct) =>
        db.NotifiedCalendarEvents.AnyAsync(
            x => x.SpaceId == spaceId && x.UserId == userId && x.EventKey == eventKey && x.EventStart == eventStart, ct);

    public async Task RecordNotifiedAsync(Guid spaceId, Guid userId, string eventKey, DateTimeOffset eventStart, CancellationToken ct)
    {
        db.NotifiedCalendarEvents.Add(new NotifiedCalendarEvent
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            UserId = userId,
            EventKey = eventKey,
            EventStart = eventStart,
            NotifiedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static IReadOnlyList<FreeBusyInterval> MergeIntervals(List<FreeBusyInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        var sorted = intervals.OrderBy(x => x.Start).ToList();
        var merged = new List<FreeBusyInterval> { sorted[0] };
        foreach (var interval in sorted.Skip(1))
        {
            var last = merged[^1];
            if (interval.Start <= last.End)
            {
                merged[^1] = new FreeBusyInterval(last.Start, interval.End > last.End ? interval.End : last.End);
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }
}
