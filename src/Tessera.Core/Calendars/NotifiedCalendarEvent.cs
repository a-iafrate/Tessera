namespace Tessera.Core.Calendars;

// Two independent things a job can proactively notify about for the same event (a reminder
// that it's starting soon, a suggestion to prep the shopping list beforehand) — Kind keeps
// their dedup rows from colliding, since one event can legitimately trigger both.
public enum CalendarNotificationKind
{
    Reminder = 0,
    ListSuggestion = 1,
}

// Calendar events have no DB row of their own (Tessera never persists them, only fetches
// live from the provider — docs/02-modello-dati.md), so idempotency for a proactive
// notification can't sit on the event itself the way Reminder.NotifiedAt does; this row is the
// closest equivalent, keyed on the event's own provider-side identity plus its current start (a
// reschedule to a new start is treated as unnotified again, on purpose).
public class NotifiedCalendarEvent
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public string EventKey { get; set; } = null!;
    public DateTimeOffset EventStart { get; set; }
    public CalendarNotificationKind Kind { get; set; }
    public DateTimeOffset NotifiedAt { get; set; }
}
