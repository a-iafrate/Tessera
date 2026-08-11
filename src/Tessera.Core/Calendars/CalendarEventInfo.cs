namespace Tessera.Core.Calendars;

// IcalUid is what dedup keys on when the same shared calendar is linked by more than one
// member (docs/02-modello-dati.md, docs/03-integrazioni.md) — null only for the rare event
// that has none, where the caller falls back to $"{CalendarId}:{ProviderEventId}".
public sealed record CalendarEventInfo(
    string ProviderEventId, string? IcalUid, string Title, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay);

public sealed record CalendarEventDraft(string Title, DateTimeOffset Start, DateTimeOffset End, string? Description);
