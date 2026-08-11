namespace Tessera.Core.Calendars;

// Calendar events have no DB row of their own (Tessera never persists them, only fetches
// live from the provider — docs/02-modello-dati.md), so idempotency for a proactive reminder
// can't sit on the event itself the way Reminder.NotifiedAt does; this row is the closest
// equivalent, keyed on the event's own provider-side identity plus its current start (a
// reschedule to a new start is treated as unnotified again, on purpose).
public class NotifiedCalendarEvent
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public string EventKey { get; set; } = null!;
    public DateTimeOffset EventStart { get; set; }
    public DateTimeOffset NotifiedAt { get; set; }
}
