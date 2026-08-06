using Tessera.Core.Reminders;

namespace Tessera.Ai.Commands;

// Shared by every command that accepts a simple recurrence (docs/02-modello-dati.md:
// deliberately poorer than RRULE — daily/weekly/monthly covers the real cases).
internal static class FrequencyKeywords
{
    public const string Pattern =
        @"daily|weekly|monthly|ogni\s+giorno|giornaliero|ogni\s+settimana|settimanale|ogni\s+mese|mensile";

    public static RecurrenceFrequency Parse(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        return normalized switch
        {
            "daily" or "giornaliero" => RecurrenceFrequency.Daily,
            "weekly" or "settimanale" => RecurrenceFrequency.Weekly,
            "monthly" or "mensile" => RecurrenceFrequency.Monthly,
            _ when normalized.Contains("giorno") => RecurrenceFrequency.Daily,
            _ when normalized.Contains("settimana") => RecurrenceFrequency.Weekly,
            _ when normalized.Contains("mese") => RecurrenceFrequency.Monthly,
            _ => RecurrenceFrequency.Daily,
        };
    }
}
