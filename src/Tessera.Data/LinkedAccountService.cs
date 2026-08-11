using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Users;

namespace Tessera.Data;

// Google OAuth handshake + refresh, plus the calendarList sync that follows a link
// (docs/02-modello-dati.md, docs/03-integrazioni.md, docs/07-compliance.md). Deliberately
// talks to Google's token endpoint directly with HttpClient rather than pulling in
// Google.Apis.Auth's flow/DataStore machinery — that machinery assumes it owns token
// persistence, and hard rule 4 requires the refresh token to go through ITokenVault
// (Key Vault) specifically, never wherever the SDK would otherwise put it.
public sealed class LinkedAccountService(
    TesseraDbContext db, ITokenVault tokenVault, ICalendarProvider calendarProvider,
    IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    // The minimum scope for each need (docs/07-compliance.md) — never the broad "calendar"
    // scope, both because it's unnecessary and because OAuth review looks favorably on the
    // narrowest request that does the job. openid/email aren't calendar scopes — they're what
    // let userinfo resolve ProviderUserId/ProviderEmail (LinkedAccount's own identity, not
    // calendar data), which is why they aren't classified as Sensitive.
    private static readonly string[] GoogleScopes =
    [
        "https://www.googleapis.com/auth/calendar.readonly",
        "https://www.googleapis.com/auth/calendar.events",
        "https://www.googleapis.com/auth/calendar.freebusy",
        "openid",
        "https://www.googleapis.com/auth/userinfo.email",
    ];

    public string BuildGoogleAuthorizationUrl(string redirectUri, string state)
    {
        var clientId = RequireConfig("Google:ClientId");
        var query = new StringBuilder("https://accounts.google.com/o/oauth2/v2/auth?")
            .Append("client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&response_type=code")
            .Append("&access_type=offline")
            .Append("&prompt=consent")
            .Append("&scope=").Append(Uri.EscapeDataString(string.Join(' ', GoogleScopes)))
            .Append("&state=").Append(Uri.EscapeDataString(state));
        return query.ToString();
    }

    public async Task<LinkedAccount> CompleteGoogleLinkAsync(Guid userId, string code, string redirectUri, CancellationToken ct)
    {
        var payload = await ExchangeAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct);

        var account = await db.LinkedAccounts.FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == ProviderKind.Google, ct);
        var secretName = $"oauth-google-{userId}";

        // Google only returns a refresh_token on first consent (or when prompt=consent forces
        // one); if a re-link somehow omits it, keep whatever was already stored rather than
        // overwrite it with nothing.
        if (payload.RefreshToken is not null)
        {
            await tokenVault.SetAsync(secretName, payload.RefreshToken, ct);
        }

        var userInfo = await GetUserInfoAsync(payload.AccessToken, ct);

        if (account is null)
        {
            account = new LinkedAccount
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Provider = ProviderKind.Google,
                ProviderUserId = userInfo.Sub,
                TokenSecretName = secretName,
                LinkedAt = DateTimeOffset.UtcNow,
            };
            db.LinkedAccounts.Add(account);
        }

        account.ProviderEmail = userInfo.Email;
        account.Scopes = GoogleScopes;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        await db.SaveChangesAsync(ct);

        await SyncCalendarsAsync(account, payload.AccessToken, ct);
        return account;
    }

    // Always refreshes rather than caching the access token in the database — the access
    // token itself must never be persisted, only the refresh token (in Key Vault) and its
    // expiry are (docs/07-compliance.md). An in-memory cache with a TTL is the documented
    // optimization once request volume justifies it; not needed yet.
    public async Task<string> GetValidAccessTokenAsync(LinkedAccount account, CancellationToken ct)
    {
        var refreshToken = await tokenVault.GetAsync(account.TokenSecretName, ct)
            ?? throw new InvalidOperationException($"No refresh token stored for linked account {account.Id}.");

        var payload = await ExchangeAsync(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        await db.SaveChangesAsync(ct);
        return payload.AccessToken;
    }

    // Unlinking means three things, in this order (docs/07-compliance.md) — deleting only the
    // local record would leave a live authorization on Google's side that the user can't see
    // or revoke from here, which is worse than confusing: it's a standing grant they don't
    // know about.
    public async Task UnlinkGoogleAsync(Guid userId, CancellationToken ct)
    {
        var account = await db.LinkedAccounts.FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == ProviderKind.Google, ct);
        if (account is null)
        {
            return;
        }

        var refreshToken = await tokenVault.GetAsync(account.TokenSecretName, ct);
        if (refreshToken is not null)
        {
            await RevokeAsync(refreshToken, ct);
        }

        await tokenVault.DeleteAsync(account.TokenSecretName, ct);

        // ExternalCalendar/CalendarSpaceMapping aren't modeled as EF-owned/cascade
        // relationships (they're looked up by LinkedAccountId, not a navigation), so the
        // cleanup is explicit — an orphaned mapping would otherwise keep a space reading a
        // calendar whose authorization no longer exists (docs/02-modello-dati.md, same
        // privacy concern as a departed member's calendar mapping).
        var calendarIds = await db.ExternalCalendars
            .Where(x => x.LinkedAccountId == account.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);
        await db.CalendarSpaceMappings.Where(x => calendarIds.Contains(x.ExternalCalendarId)).ExecuteDeleteAsync(ct);
        await db.ExternalCalendars.Where(x => x.LinkedAccountId == account.Id).ExecuteDeleteAsync(ct);

        db.LinkedAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeAsync(string token, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            "https://oauth2.googleapis.com/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        // Google returns 200 even if the token was already invalid — a non-success here means
        // the request itself was malformed, not that the token is somehow still valid, so
        // there's nothing useful to retry; log-and-continue would need an ILogger this service
        // doesn't take, so this simply doesn't block local cleanup on it.
    }

    public Task<bool> IsGoogleLinkedAsync(Guid userId, CancellationToken ct) =>
        db.LinkedAccounts.AnyAsync(x => x.UserId == userId && x.Provider == ProviderKind.Google, ct);

    public async Task<IReadOnlyList<ExternalCalendar>> GetCalendarsAsync(Guid userId, CancellationToken ct)
    {
        var accountIds = await db.LinkedAccounts.Where(x => x.UserId == userId).Select(x => x.Id).ToListAsync(ct);
        return await db.ExternalCalendars
            .Where(x => accountIds.Contains(x.LinkedAccountId))
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task SetCalendarEnabledAsync(Guid userId, Guid externalCalendarId, bool enabled, CancellationToken ct)
    {
        var accountIds = await db.LinkedAccounts.Where(x => x.UserId == userId).Select(x => x.Id).ToListAsync(ct);
        var calendar = await db.ExternalCalendars
            .FirstOrDefaultAsync(x => x.Id == externalCalendarId && accountIds.Contains(x.LinkedAccountId), ct);
        if (calendar is null)
        {
            return;
        }

        calendar.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    private async Task SyncCalendarsAsync(LinkedAccount account, string accessToken, CancellationToken ct)
    {
        var providerCalendars = await calendarProvider.ListCalendarsAsync(accessToken, ct);
        var existing = await db.ExternalCalendars.Where(x => x.LinkedAccountId == account.Id).ToListAsync(ct);
        var existingById = existing.ToDictionary(x => x.ProviderCalendarId);

        foreach (var info in providerCalendars)
        {
            if (existingById.TryGetValue(info.ProviderCalendarId, out var calendar))
            {
                calendar.Name = info.Name;
                calendar.Color = info.Color;
                calendar.IsPrimary = info.IsPrimary;
                calendar.ProviderRole = info.ProviderRole;
                calendar.LastSyncedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                // Only the primary calendar defaults to enabled (docs/02-modello-dati.md) —
                // enabling every calendar the account happens to expose ("Holidays",
                // forgotten subscriptions) produces an unusable view on first link.
                db.ExternalCalendars.Add(new ExternalCalendar
                {
                    Id = Guid.NewGuid(),
                    LinkedAccountId = account.Id,
                    ProviderCalendarId = info.ProviderCalendarId,
                    Name = info.Name,
                    Color = info.Color,
                    IsPrimary = info.IsPrimary,
                    ProviderRole = info.ProviderRole,
                    IsEnabled = info.IsPrimary,
                    LastSyncedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<GoogleTokenResponse> ExchangeAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        parameters["client_id"] = RequireConfig("Google:ClientId");
        parameters["client_secret"] = RequireConfig("Google:ClientSecret");

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(parameters), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Google's token endpoint returned an empty response.");
    }

    // LinkedAccount.ProviderUserId identifies *this* Google account among however many the
    // same Tessera user might link over time — it has nothing to do with calendar data, hence
    // the separate openid/userinfo.email scopes rather than reusing a calendar call.
    private async Task<GoogleUserInfo> GetUserInfoAsync(string accessToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<GoogleUserInfo>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Google's userinfo endpoint returned an empty response.");
    }

    private string RequireConfig(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is not configured.");

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record GoogleUserInfo(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email);
}
