using System.Net.Http.Headers;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Users;

namespace Tessera.Integrations;

// Wraps the Calendar v3 SDK behind ICalendarProvider — the access token is already valid by
// the time it gets here; refreshing it from the stored refresh token is LinkedAccountService's
// job (Tessera.Data), not this client's (docs/01-architettura.md).
public sealed class GoogleCalendarClient : ICalendarProvider
{
    public ProviderKind Provider => ProviderKind.Google;

    public async Task<IReadOnlyList<ProviderCalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        using var service = CreateService(accessToken);
        var result = await service.CalendarList.List().ExecuteAsync(ct);

        return (result.Items ?? [])
            .Select(x => new ProviderCalendarInfo(
                x.Id,
                x.SummaryOverride ?? x.Summary ?? x.Id,
                x.BackgroundColor,
                x.Primary ?? false,
                MapAccessRole(x.AccessRole)))
            .ToList();
    }

    // Only ever used for CalendarShareLevel.Availability — never events.list, so titles can
    // never leak into a response meant to carry only busy/free slots (docs/07-compliance.md,
    // docs/03-integrazioni.md).
    public async Task<IReadOnlyList<FreeBusyInterval>> GetFreeBusyAsync(
        string accessToken, IReadOnlyList<string> providerCalendarIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var service = CreateService(accessToken);
        var request = new FreeBusyRequest
        {
            TimeMinDateTimeOffset = from,
            TimeMaxDateTimeOffset = to,
            Items = providerCalendarIds.Select(id => new FreeBusyRequestItem { Id = id }).ToList(),
        };

        var response = await service.Freebusy.Query(request).ExecuteAsync(ct);
        return (response.Calendars ?? new Dictionary<string, FreeBusyCalendar>())
            .SelectMany(kvp => kvp.Value.Busy ?? [])
            .Where(x => x.StartDateTimeOffset is not null && x.EndDateTimeOffset is not null)
            .Select(x => new FreeBusyInterval(x.StartDateTimeOffset!.Value, x.EndDateTimeOffset!.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<CalendarEventInfo>> GetEventsAsync(
        string accessToken, string providerCalendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var service = CreateService(accessToken);
        var request = service.Events.List(providerCalendarId);
        request.TimeMinDateTimeOffset = from;
        request.TimeMaxDateTimeOffset = to;
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        var result = await request.ExecuteAsync(ct);
        return (result.Items ?? []).Select(MapEvent).ToList();
    }

    public async Task<CalendarEventInfo> CreateEventAsync(
        string accessToken, string providerCalendarId, CalendarEventDraft draft, CancellationToken ct)
    {
        using var service = CreateService(accessToken);
        var newEvent = new Event
        {
            Summary = draft.Title,
            Description = draft.Description,
            Start = new EventDateTime { DateTimeDateTimeOffset = draft.Start },
            End = new EventDateTime { DateTimeDateTimeOffset = draft.End },
        };

        var created = await service.Events.Insert(newEvent, providerCalendarId).ExecuteAsync(ct);
        return MapEvent(created);
    }

    private static CalendarEventInfo MapEvent(Event source) => new(
        source.Id,
        source.ICalUID,
        source.Summary ?? "",
        source.Start?.DateTimeDateTimeOffset ?? DateTimeOffset.Parse(source.Start!.Date!),
        source.End?.DateTimeDateTimeOffset ?? DateTimeOffset.Parse(source.End!.Date!),
        source.Start?.DateTimeDateTimeOffset is null);

    private static ProviderAccessRole MapAccessRole(string? accessRole) => accessRole switch
    {
        "owner" => ProviderAccessRole.Owner,
        "writer" => ProviderAccessRole.Writer,
        "reader" => ProviderAccessRole.Reader,
        _ => ProviderAccessRole.FreeBusyReader,
    };

    // Deliberately not GoogleCredential.FromAccessToken: that wraps a full OAuth2 credential
    // that tries to manage its own refresh, and a bare access token with no refresh flow
    // behind it makes that machinery throw (wrapped in AggregateException from an internal
    // blocking call) before a single request even goes out. Token lifecycle is already
    // LinkedAccountService's job — this just needs to attach the header.
    private static CalendarService CreateService(string accessToken) => new(new BaseClientService.Initializer
    {
        HttpClientInitializer = new BearerTokenInitializer(accessToken),
        ApplicationName = "Tessera",
    });

    private sealed class BearerTokenInitializer(string accessToken) : IConfigurableHttpClientInitializer
    {
        public void Initialize(ConfigurableHttpClient httpClient) =>
            httpClient.MessageHandler.AddExecuteInterceptor(new BearerTokenInterceptor(accessToken));
    }

    private sealed class BearerTokenInterceptor(string accessToken) : IHttpExecuteInterceptor
    {
        public Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return Task.CompletedTask;
        }
    }
}
