namespace Tessera.Core.Calendars;

// One calendar exposed by a LinkedAccount — a single Google/Microsoft account exposes many
// (personal, shared family, birthdays, work), so sharing granularity has to sit on the
// calendar, not the account (docs/02-modello-dati.md).
public class ExternalCalendar
{
    public Guid Id { get; set; }
    public Guid LinkedAccountId { get; set; }
    public string ProviderCalendarId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public bool IsPrimary { get; set; }
    public ProviderAccessRole ProviderRole { get; set; }

    // Only the primary calendar defaults to enabled (docs/02-modello-dati.md) — enabling
    // everything on first link produces an unusable merged view exactly when the user is
    // judging the feature.
    public bool IsEnabled { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
}
