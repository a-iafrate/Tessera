namespace Tessera.Core.Calendars;

// One row from the provider's calendarList — the raw material ExternalCalendar rows get
// populated/refreshed from (docs/03-integrazioni.md).
public sealed record ProviderCalendarInfo(
    string ProviderCalendarId, string Name, string? Color, bool IsPrimary, ProviderAccessRole ProviderRole);
