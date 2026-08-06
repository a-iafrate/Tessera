namespace Tessera.Core.Reminders;

// Deliberately poorer than RRULE (RFC 5545) — covers real reminder/fixed-expense cases
// without the complexity of a full recurrence engine (docs/02-modello-dati.md).
public class RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; set; }
    public int Interval { get; set; } = 1;
    public DayOfWeek[]? DaysOfWeek { get; set; }
    public int? DayOfMonth { get; set; }

    // Shared by reminder creation (rolling a past first-occurrence forward) and the
    // scheduled job (advancing after each firing) — one definition of "next occurrence"
    // for the simplified frequency model this class supports (Interval/DaysOfWeek/DayOfMonth
    // aren't factored in yet, matching the class-level note above).
    public static DateTimeOffset Advance(DateTimeOffset from, RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => from.AddDays(1),
        RecurrenceFrequency.Weekly => from.AddDays(7),
        RecurrenceFrequency.Monthly => from.AddMonths(1),
        RecurrenceFrequency.Yearly => from.AddYears(1),
        _ => from.AddDays(1),
    };
}
