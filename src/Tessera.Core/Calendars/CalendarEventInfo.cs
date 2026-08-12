namespace Tessera.Core.Calendars;

// IcalUid is what dedup keys on when the same shared calendar is linked by more than one
// member (docs/02-modello-dati.md, docs/03-integrazioni.md) — null only for the rare event
// that has none, where the caller falls back to $"{CalendarId}:{ProviderEventId}".
//
// ExternalCalendarId isn't known to ICalendarProvider (it only ever sees provider-side ids) —
// CalendarQueryService stamps it on after the fact, via `with`, so a caller can later trace a
// specific event back to the LinkedAccount that has to perform a write against it (e.g.
// deleting it). Defaults to Guid.Empty for provider implementations, which never set it.
public sealed record CalendarEventInfo(
    string ProviderEventId, string? IcalUid, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay,
    Guid ExternalCalendarId = default);

public sealed record CalendarEventDraft(string Title, DateTimeOffset Start, DateTimeOffset End, string? Description);
