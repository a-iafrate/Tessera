using System.Security.Claims;
using System.Security.Cryptography;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Endpoints;

// One route pair for every calendar provider (docs/03-integrazioni.md) — the CSRF-state
// cookie dance, redirect-uri construction, and error handling are identical regardless of
// which provider's consent screen the browser is sent to, so this stays a single file rather
// than one near-duplicate per provider.
public static class CalendarOAuthEndpoints
{
    public static IEndpointRouteBuilder MapCalendarOAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/oauth/{provider}/link", HandleLinkAsync).RequireAuthorization();
        endpoints.MapGet("/oauth/{provider}/callback", HandleCallbackAsync).RequireAuthorization();
        return endpoints;
    }

    // GET rather than a Blazor button click: the browser needs to navigate away to the
    // provider's consent screen, which a SignalR-circuit event can't do on its own.
    private static IResult HandleLinkAsync(HttpContext context, LinkedAccountService linkedAccounts, string provider)
    {
        if (!TryParseProvider(provider, out var providerKind))
        {
            return Results.NotFound();
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(StateCookieName(providerKind), state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
        });

        var redirectUri = BuildRedirectUri(context, providerKind);
        var authorizationUrl = linkedAccounts.BuildAuthorizationUrl(providerKind, redirectUri, state);
        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> HandleCallbackAsync(
        HttpContext context, LinkedAccountService linkedAccounts, ILogger<LinkedAccountService> logger,
        string provider, string? code, string? state, string? error, CancellationToken ct)
    {
        if (!TryParseProvider(provider, out var providerKind))
        {
            return Results.NotFound();
        }

        var cookieName = StateCookieName(providerKind);
        context.Response.Cookies.Delete(cookieName);

        var providerSegment = provider.ToLowerInvariant();

        if (error is not null)
        {
            return Results.Redirect($"/calendars?error=denied&provider={providerSegment}");
        }

        var expectedState = context.Request.Cookies[cookieName];
        if (code is null || state is null || expectedState is null || state != expectedState)
        {
            return Results.Redirect($"/calendars?error=state&provider={providerSegment}");
        }

        var userId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var redirectUri = BuildRedirectUri(context, providerKind);

        try
        {
            await linkedAccounts.CompleteLinkAsync(providerKind, userId, code, redirectUri, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Provider} Calendar link failed for user {UserId}", providerKind, userId);
            return Results.Redirect($"/calendars?error=exchange&provider={providerSegment}");
        }

        return Results.Redirect("/calendars");
    }

    private static bool TryParseProvider(string provider, out ProviderKind providerKind)
    {
        switch (provider.ToLowerInvariant())
        {
            case "google":
                providerKind = ProviderKind.Google;
                return true;
            case "microsoft":
                providerKind = ProviderKind.Microsoft;
                return true;
            default:
                providerKind = default;
                return false;
        }
    }

    private static string StateCookieName(ProviderKind provider) =>
        $"oauth_state_{provider.ToString().ToLowerInvariant()}";

    // Must exactly match what's registered in each provider's own app console (Google Cloud
    // Console / Entra ID app registration) — built from the current request rather than
    // hardcoded so the same code works for local dev and production, as long as both are
    // registered there.
    private static string BuildRedirectUri(HttpContext context, ProviderKind provider) =>
        $"{context.Request.Scheme}://{context.Request.Host}/oauth/{provider.ToString().ToLowerInvariant()}/callback";
}
