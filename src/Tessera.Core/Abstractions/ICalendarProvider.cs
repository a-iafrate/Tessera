using Tessera.Core.Calendars;
using Tessera.Core.Users;

namespace Tessera.Core.Abstractions;

// One implementation per provider (Google, Microsoft Graph — docs/01-architettura.md). Takes
// an already-valid access token rather than a LinkedAccount: resolving/refreshing the token
// from Key Vault is an infrastructure concern the caller (Tessera.Data) owns, keeping this
// interface — and everything that depends only on it — free of Key Vault/HTTP knowledge.
public interface ICalendarProvider
{
    ProviderKind Provider { get; }

    Task<IReadOnlyList<ProviderCalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct);

    Task<IReadOnlyList<FreeBusyInterval>> GetFreeBusyAsync(
        string accessToken, IReadOnlyList<string> providerCalendarIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<IReadOnlyList<CalendarEventInfo>> GetEventsAsync(
        string accessToken, string providerCalendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    Task<CalendarEventInfo> CreateEventAsync(
        string accessToken, string providerCalendarId, CalendarEventDraft draft, CancellationToken ct);
}
