namespace Tessera.Core.Calendars;

// A busy slot with no title attached — the only shape CalendarShareLevel.Availability is
// allowed to see (docs/02-modello-dati.md, docs/07-compliance.md: freebusy.query returns
// exactly this and nothing else).
public sealed record FreeBusyInterval(DateTimeOffset Start, DateTimeOffset End);
