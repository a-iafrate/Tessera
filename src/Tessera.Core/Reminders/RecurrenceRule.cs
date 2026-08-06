namespace Tessera.Core.Reminders;

// Deliberately poorer than RRULE (RFC 5545) — covers real reminder/fixed-expense cases
// without the complexity of a full recurrence engine (docs/02-modello-dati.md).
public class RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; } = 1;
    public DayOfWeek[]? DaysOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
}
