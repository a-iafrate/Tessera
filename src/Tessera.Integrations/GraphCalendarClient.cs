using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Users;

namespace Tessera.Integrations;

// Hand-rolled HttpClient calls against Microsoft Graph's REST API rather than the Microsoft.Graph
// SDK — the SDK is a large dependency for four endpoints, and this mirrors how
// LinkedAccountService already talks to every provider's OAuth endpoints directly (no
// Microsoft.Identity.Client either: refresh is LinkedAccountService's job, this only ever sees
// an already-valid access token, docs/01-architettura.md).
public sealed class GraphCalendarClient(IHttpClientFactory httpClientFactory, ILogger<GraphCalendarClient> logger) : ICalendarProvider
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";

    public ProviderKind Provider => ProviderKind.Microsoft;

    public async Task<IReadOnlyList<ProviderCalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        var result = await GetAsync<GraphListResponse<GraphCalendar>>(accessToken, $"{BaseUrl}/me/calendars", ct);
        return (result.Value)
            .Select(x => new ProviderCalendarInfo(
                x.Id,
                x.Name,
                x.Color,
                x.IsDefaultCalendar ?? false,
                MapAccessRole(x)))
            .ToList();
    }

    // Never requests "subject"/"body" — $select limits what Graph even puts on the wire, so no
    // event title can leak into a response meant to carry only busy/free slots
    // (docs/07-compliance.md, docs/03-integrazioni.md). Graph has no calendar-scoped equivalent
    // of Google's freebusy.query (getSchedule works over mailboxes, not arbitrary calendar
    // ids — docs/03-integrazioni.md), so this reads each calendar's own view and keeps only the
    // interval.
    public async Task<IReadOnlyList<FreeBusyInterval>> GetFreeBusyAsync(
        string accessToken, IReadOnlyList<string> providerCalendarIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var intervals = new List<FreeBusyInterval>();
        foreach (var calendarId in providerCalendarIds)
        {
            var url = $"{BaseUrl}/me/calendars/{calendarId}/calendarView"
                + $"?startDateTime={Uri.EscapeDataString(FormatGraphDateTime(from))}"
                + $"&endDateTime={Uri.EscapeDataString(FormatGraphDateTime(to))}"
                + "&$select=start,end";
            var result = await GetAsync<GraphListResponse<GraphEvent>>(accessToken, url, ct);
            intervals.AddRange(result.Value.Select(x => new FreeBusyInterval(ParseGraphDateTime(x.Start), ParseGraphDateTime(x.End))));
        }

        return intervals;
    }

    public async Task<IReadOnlyList<CalendarEventInfo>> GetEventsAsync(
        string accessToken, string providerCalendarId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var url = $"{BaseUrl}/me/calendars/{providerCalendarId}/calendarView"
            + $"?startDateTime={Uri.EscapeDataString(FormatGraphDateTime(from))}"
            + $"&endDateTime={Uri.EscapeDataString(FormatGraphDateTime(to))}"
            + "&$select=id,iCalUId,subject,start,end,isAllDay&$orderby=start/dateTime";
        var result = await GetAsync<GraphListResponse<GraphEvent>>(accessToken, url, ct);
        return result.Value.Select(MapEvent).ToList();
    }

    public async Task<CalendarEventInfo> CreateEventAsync(
        string accessToken, string providerCalendarId, CalendarEventDraft draft, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/me/calendars/{providerCalendarId}/events")
        {
            Content = JsonContent.Create(new GraphEventCreate(
                draft.Title,
                draft.Description is null ? null : new GraphItemBody("text", draft.Description),
                new GraphDateTimeTimeZone(FormatGraphDateTime(draft.Start), "UTC"),
                new GraphDateTimeTimeZone(FormatGraphDateTime(draft.End), "UTC"))),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var created = await response.Content.ReadFromJsonAsync<GraphEvent>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Microsoft Graph's create-event endpoint returned an empty response.");
        return MapEvent(created);
    }

    public async Task DeleteEventAsync(string accessToken, string providerCalendarId, string providerEventId, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/me/calendars/{providerCalendarId}/events/{providerEventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // PATCH only the two fields being changed — same reasoning as Google's Patch vs Update:
    // sending a full replacement body risks clearing fields (subject, body) this call never
    // touches.
    public async Task<CalendarEventInfo> MoveEventAsync(
        string accessToken, string providerCalendarId, string providerEventId, DateTimeOffset newStart, DateTimeOffset newEnd, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{BaseUrl}/me/calendars/{providerCalendarId}/events/{providerEventId}")
        {
            Content = JsonContent.Create(new GraphEventReschedule(
                new GraphDateTimeTimeZone(FormatGraphDateTime(newStart), "UTC"),
                new GraphDateTimeTimeZone(FormatGraphDateTime(newEnd), "UTC"))),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        var updated = await response.Content.ReadFromJsonAsync<GraphEvent>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Microsoft Graph's update-event endpoint returned an empty response.");
        return MapEvent(updated);
    }

    private async Task<T> GetAsync<T>(string accessToken, string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Microsoft Graph returned an empty response for {url}.");
    }

    // Graph's error body (an "error.code"/"error.message" JSON object) is the only way to tell
    // a malformed request apart from an expired token or a missing permission — all three
    // surface as HttpRequestException with nothing but a status code once EnsureSuccessStatusCode
    // runs, so the body has to be logged before that happens or the reason is lost.
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogError(
            "Microsoft Graph request to {Method} {Url} failed with {StatusCode}: {Body}",
            response.RequestMessage?.Method, response.RequestMessage?.RequestUri, response.StatusCode, body);
        response.EnsureSuccessStatusCode();
    }

    private static CalendarEventInfo MapEvent(GraphEvent source) => new(
        source.Id,
        source.ICalUId,
        source.Subject ?? "",
        ParseGraphDateTime(source.Start),
        ParseGraphDateTime(source.End),
        source.IsAllDay ?? false);

    // Requests always specify "UTC" for both the outgoing dateTime and the query window, and
    // Graph defaults to returning event times in UTC when no Prefer: outlook.timezone header is
    // sent — so every dateTime string here is treated as UTC, never the server's or a caller's
    // local zone (docs/03-integrazioni.md: never assume the server's timezone).
    private static string FormatGraphDateTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

    private static DateTimeOffset ParseGraphDateTime(GraphDateTimeTimeZone value) =>
        new(DateTime.Parse(value.DateTime), TimeSpan.Zero);

    // Graph exposes permission per calendar as canEdit/canShare rather than Google's single
    // accessRole string (docs/03-integrazioni.md) — the mapping has to be written by hand: the
    // account's own default calendar is always fully owned, any other editable calendar is a
    // Writer, and anything else falls back to the weakest level the abstraction supports.
    private static ProviderAccessRole MapAccessRole(GraphCalendar calendar) => calendar switch
    {
        { IsDefaultCalendar: true } => ProviderAccessRole.Owner,
        { CanEdit: true } => ProviderAccessRole.Writer,
        _ => ProviderAccessRole.FreeBusyReader,
    };

    private sealed record GraphListResponse<T>([property: JsonPropertyName("value")] List<T> Value);

    private sealed record GraphCalendar(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("canEdit")] bool? CanEdit,
        [property: JsonPropertyName("isDefaultCalendar")] bool? IsDefaultCalendar);

    private sealed record GraphEvent(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("iCalUId")] string? ICalUId,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("start")] GraphDateTimeTimeZone Start,
        [property: JsonPropertyName("end")] GraphDateTimeTimeZone End,
        [property: JsonPropertyName("isAllDay")] bool? IsAllDay);

    private sealed record GraphDateTimeTimeZone(
        [property: JsonPropertyName("dateTime")] string DateTime,
        [property: JsonPropertyName("timeZone")] string TimeZone);

    private sealed record GraphEventReschedule(
        [property: JsonPropertyName("start")] GraphDateTimeTimeZone Start,
        [property: JsonPropertyName("end")] GraphDateTimeTimeZone End);

    private sealed record GraphItemBody(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GraphEventCreate(
        [property: JsonPropertyName("subject")] string Subject,
        // Graph rejects the request outright ("ErrorInvalidRequest: The body of the item is
        // invalid") if this complex property is present as a JSON null rather than omitted —
        // WhenWritingNull drops the key entirely for a title-only event.
        [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GraphItemBody? Body,
        [property: JsonPropertyName("start")] GraphDateTimeTimeZone Start,
        [property: JsonPropertyName("end")] GraphDateTimeTimeZone End);
}
