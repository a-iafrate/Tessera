namespace Tessera.Core.Calendars;

// Every enabled calendar can be exposed to one or more spaces, each with its own level — the
// same personal calendar can be Details+Write in "Personal" and Availability-only in "Football
// Team" (docs/02-modello-dati.md).
public class CalendarSpaceMapping
{
    public Guid ExternalCalendarId { get; set; }
    public Guid SpaceId { get; set; }
    public CalendarShareLevel ShareLevel { get; set; }

    // Resolves "where do I create the event?" — a space with three writable calendars needs a
    // default target, or every creation becomes a question (docs/02-modello-dati.md).
    public bool IsDefaultWriteTarget { get; set; }
}
