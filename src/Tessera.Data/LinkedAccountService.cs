using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Users;

namespace Tessera.Data;

// OAuth handshake + refresh for every linkable calendar provider, plus the calendarList sync
// that follows a link (docs/02-modello-dati.md, docs/03-integrazioni.md, docs/07-compliance.md).
// Deliberately talks to each provider's token endpoint directly with HttpClient rather than
// pulling in a provider SDK's own flow/DataStore machinery — that machinery assumes it owns
// token persistence, and hard rule 4 requires the refresh token to go through ITokenVault
// (Key Vault) specifically, never wherever an SDK would otherwise put it.
public sealed class LinkedAccountService(
    TesseraDbContext db, ITokenVault tokenVault, IEnumerable<ICalendarProvider> calendarProviders,
    IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    // The minimum scope for each need (docs/07-compliance.md) — never a broad "full calendar"
    // grant, both because it's unnecessary and because OAuth review looks favorably on the
    // narrowest request that does the job. openid/email aren't calendar scopes — they're what
    // let userinfo resolve ProviderUserId/ProviderEmail (LinkedAccount's own identity, not
    // calendar data), which is why they aren't classified as Sensitive.
    private static readonly ProviderOAuthConfig GoogleConfig = new(
        AuthorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint: "https://oauth2.googleapis.com/token",
        UserInfoEndpoint: "https://www.googleapis.com/oauth2/v3/userinfo",
        RevokeEndpoint: "https://oauth2.googleapis.com/revoke",
        ClientIdConfigKey: "Google:ClientId",
        ClientSecretConfigKey: "Google:ClientSecret",
        Scopes:
        [
            "https://www.googleapis.com/auth/calendar.readonly",
            "https://www.googleapis.com/auth/calendar.events",
            "https://www.googleapis.com/auth/calendar.freebusy",
            "openid",
            "https://www.googleapis.com/auth/userinfo.email",
        ],
        ExtraAuthorizationParameters: [("access_type", "offline"), ("prompt", "consent")]);

    // docs/03-integrazioni.md: Microsoft has no per-token revoke endpoint the way Google
    // does — RevokeEndpoint is null, and UnlinkAsync only cleans up locally for this provider
    // (still deletes Key Vault's copy of the refresh token, which is the part hard rule 4
    // actually requires). "common" accepts both personal Microsoft accounts and work/school
    // tenants, matching the doc's requirement that either kind of account can consent.
    private static readonly ProviderOAuthConfig MicrosoftConfig = new(
        AuthorizationEndpoint: "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenEndpoint: "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        UserInfoEndpoint: "https://graph.microsoft.com/v1.0/me",
        RevokeEndpoint: null,
        ClientIdConfigKey: "Microsoft:ClientId",
        ClientSecretConfigKey: "Microsoft:ClientSecret",
        Scopes:
        [
            "offline_access",
            "Calendars.Read",
            "Calendars.ReadWrite",
            "Calendars.Read.Shared",
            "openid",
            "email",
            // GetUserInfoAsync's call to /me needs this explicitly granted to the token — the
            // app having it under API permissions in Entra ID isn't enough, since Microsoft
            // only consents to what's actually listed in the authorization request's scope.
            "User.Read",
        ],
        ExtraAuthorizationParameters: [("response_mode", "query")]);

    private static ProviderOAuthConfig GetConfig(ProviderKind provider) => provider switch
    {
        ProviderKind.Google => GoogleConfig,
        ProviderKind.Microsoft => MicrosoftConfig,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown calendar provider."),
    };

    public string BuildAuthorizationUrl(ProviderKind provider, string redirectUri, string state)
    {
        var config = GetConfig(provider);
        var clientId = RequireConfig(config.ClientIdConfigKey);
        var query = new StringBuilder(config.AuthorizationEndpoint)
            .Append('?')
            .Append("client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&response_type=code")
            .Append("&scope=").Append(Uri.EscapeDataString(string.Join(' ', config.Scopes)))
            .Append("&state=").Append(Uri.EscapeDataString(state));

        foreach (var (key, value) in config.ExtraAuthorizationParameters)
        {
            query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        return query.ToString();
    }

    public async Task<LinkedAccount> CompleteLinkAsync(ProviderKind provider, Guid userId, string code, string redirectUri, CancellationToken ct)
    {
        var config = GetConfig(provider);
        var payload = await ExchangeAsync(config, new Dictionary<string, string>
        {
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        }, ct);

        var account = await db.LinkedAccounts.FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == provider, ct);
        var secretName = $"oauth-{provider.ToString().ToLowerInvariant()}-{userId}";

        // Not every exchange returns a refresh_token (Google only on first consent, or when
        // prompt=consent forces one); if a re-link somehow omits it, keep whatever was already
        // stored rather than overwrite it with nothing.
        if (payload.RefreshToken is not null)
        {
            await tokenVault.SetAsync(secretName, payload.RefreshToken, ct);
        }

        var userInfo = await GetUserInfoAsync(provider, payload.AccessToken, ct);

        if (account is null)
        {
            account = new LinkedAccount
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Provider = provider,
                ProviderUserId = userInfo.Id,
                TokenSecretName = secretName,
                LinkedAt = DateTimeOffset.UtcNow,
            };
            db.LinkedAccounts.Add(account);
        }

        account.ProviderEmail = userInfo.Email;
        account.Scopes = config.Scopes;
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
        var config = GetConfig(account.Provider);
        var refreshToken = await tokenVault.GetAsync(account.TokenSecretName, ct)
            ?? throw new InvalidOperationException($"No refresh token stored for linked account {account.Id}.");

        var payload = await ExchangeAsync(config, new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

        // Microsoft rotates the refresh token on every use (unlike Google, which only ever
        // returns one on first consent) — the old one may already be invalid on the provider's
        // side by the time this returns, so a new one in the response has to replace it in Key
        // Vault immediately or the next refresh fails.
        if (payload.RefreshToken is not null)
        {
            await tokenVault.SetAsync(account.TokenSecretName, payload.RefreshToken, ct);
        }

        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
        await db.SaveChangesAsync(ct);
        return payload.AccessToken;
    }

    // Unlinking means (docs/07-compliance.md): revoke on the provider's side where that's
    // possible, delete the refresh token from Key Vault, then delete the local records —
    // deleting only the local record would leave a live authorization on the provider's side
    // that the user can't see or revoke from here, which is worse than confusing: it's a
    // standing grant they don't know about.
    public async Task UnlinkAsync(ProviderKind provider, Guid userId, CancellationToken ct)
    {
        var config = GetConfig(provider);
        var account = await db.LinkedAccounts.FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == provider, ct);
        if (account is null)
        {
            return;
        }

        if (config.RevokeEndpoint is not null)
        {
            var refreshToken = await tokenVault.GetAsync(account.TokenSecretName, ct);
            if (refreshToken is not null)
            {
                await RevokeAsync(config.RevokeEndpoint, refreshToken, ct);
            }
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

    private async Task RevokeAsync(string revokeEndpoint, string token, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            revokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        // The provider returns success even if the token was already invalid — a non-success
        // here means the request itself was malformed, not that the token is somehow still
        // valid, so there's nothing useful to retry; log-and-continue would need an ILogger
        // this service doesn't take, so this simply doesn't block local cleanup on it.
    }

    // Re-fetches the provider's calendar list for one linked account — accessRole and calendar
    // sharing can change on the provider's side after the initial link (a calendar unshared, a
    // role downgraded from Writer to Reader), and nothing else calls back into the provider
    // once linking is done, so RefreshCalendarListJob is the only place this happens
    // periodically.
    public async Task RefreshCalendarsAsync(LinkedAccount account, CancellationToken ct)
    {
        var accessToken = await GetValidAccessTokenAsync(account, ct);
        await SyncCalendarsAsync(account, accessToken, ct);
    }

    public Task<bool> IsLinkedAsync(Guid userId, ProviderKind provider, CancellationToken ct) =>
        db.LinkedAccounts.AnyAsync(x => x.UserId == userId && x.Provider == provider, ct);

    public async Task<IReadOnlyList<ExternalCalendar>> GetCalendarsAsync(Guid userId, ProviderKind provider, CancellationToken ct)
    {
        var accountIds = await db.LinkedAccounts
            .Where(x => x.UserId == userId && x.Provider == provider)
            .Select(x => x.Id)
            .ToListAsync(ct);
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
        var provider = calendarProviders.First(x => x.Provider == account.Provider);
        var providerCalendars = await provider.ListCalendarsAsync(accessToken, ct);
        var providerIds = providerCalendars.Select(x => x.ProviderCalendarId).ToHashSet();
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

        // A calendar the provider no longer returns (unshared, deleted, access revoked) can't
        // keep backing a space's mapping — the same cleanup UnlinkAsync does for a whole
        // account, scoped here to just the calendars that disappeared.
        var removedIds = existing.Where(x => !providerIds.Contains(x.ProviderCalendarId)).Select(x => x.Id).ToList();
        if (removedIds.Count > 0)
        {
            await db.CalendarSpaceMappings.Where(x => removedIds.Contains(x.ExternalCalendarId)).ExecuteDeleteAsync(ct);
            await db.ExternalCalendars.Where(x => removedIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<TokenResponse> ExchangeAsync(ProviderOAuthConfig config, Dictionary<string, string> parameters, CancellationToken ct)
    {
        parameters["client_id"] = RequireConfig(config.ClientIdConfigKey);
        parameters["client_secret"] = RequireConfig(config.ClientSecretConfigKey);

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(parameters), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"{config.TokenEndpoint} returned an empty response.");
    }

    // LinkedAccount.ProviderUserId identifies *this* provider account among however many the
    // same Tessera user might link over time — it has nothing to do with calendar data, hence
    // the separate openid/email scopes rather than reusing a calendar call. Google's userinfo
    // endpoint uses "sub"/"email"; Graph's /me uses "id"/"mail", falling back to
    // userPrincipalName for personal Microsoft accounts, which often have no "mail" claim.
    private async Task<ProviderUserInfo> GetUserInfoAsync(ProviderKind provider, string accessToken, CancellationToken ct)
    {
        var config = GetConfig(provider);
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, config.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return provider switch
        {
            ProviderKind.Google => await ReadGoogleUserInfoAsync(response, ct),
            ProviderKind.Microsoft => await ReadMicrosoftUserInfoAsync(response, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown calendar provider."),
        };
    }

    private static async Task<ProviderUserInfo> ReadGoogleUserInfoAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<GoogleUserInfo>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Google's userinfo endpoint returned an empty response.");
        return new ProviderUserInfo(body.Sub, body.Email);
    }

    private static async Task<ProviderUserInfo> ReadMicrosoftUserInfoAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<MicrosoftUserInfo>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Microsoft Graph's /me endpoint returned an empty response.");
        return new ProviderUserInfo(body.Id, body.Mail ?? body.UserPrincipalName);
    }

    private string RequireConfig(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is not configured.");

    private sealed record ProviderOAuthConfig(
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string UserInfoEndpoint,
        string? RevokeEndpoint,
        string ClientIdConfigKey,
        string ClientSecretConfigKey,
        string[] Scopes,
        (string Key, string Value)[] ExtraAuthorizationParameters);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed record GoogleUserInfo(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email);

    private sealed record MicrosoftUserInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("mail")] string? Mail,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName);

    private sealed record ProviderUserInfo(string Id, string? Email);
}
