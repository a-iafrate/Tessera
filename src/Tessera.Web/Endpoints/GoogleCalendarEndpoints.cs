using System.Security.Claims;
using System.Security.Cryptography;
using Tessera.Data;

namespace Tessera.Web.Endpoints;

public static class GoogleCalendarEndpoints
{
    private const string StateCookieName = "google_oauth_state";

    public static IEndpointRouteBuilder MapGoogleCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/oauth/google/link", HandleLinkAsync).RequireAuthorization();
        endpoints.MapGet("/oauth/google/callback", HandleCallbackAsync).RequireAuthorization();
        return endpoints;
    }

    // GET rather than a Blazor button click: the browser needs to navigate away to Google's
    // consent screen, which a SignalR-circuit event can't do on its own.
    private static IResult HandleLinkAsync(HttpContext context, LinkedAccountService linkedAccounts)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
        });

        var redirectUri = BuildRedirectUri(context);
        var authorizationUrl = linkedAccounts.BuildGoogleAuthorizationUrl(redirectUri, state);
        return Results.Redirect(authorizationUrl);
    }

    private static async Task<IResult> HandleCallbackAsync(
        HttpContext context, LinkedAccountService linkedAccounts, ILogger<LinkedAccountService> logger,
        string? code, string? state, string? error, CancellationToken ct)
    {
        context.Response.Cookies.Delete(StateCookieName);

        if (error is not null)
        {
            return Results.Redirect("/calendars?error=denied");
        }

        var expectedState = context.Request.Cookies[StateCookieName];
        if (code is null || state is null || expectedState is null || state != expectedState)
        {
            return Results.Redirect("/calendars?error=state");
        }

        var userId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var redirectUri = BuildRedirectUri(context);

        try
        {
            await linkedAccounts.CompleteGoogleLinkAsync(userId, code, redirectUri, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Google Calendar link failed for user {UserId}", userId);
            return Results.Redirect("/calendars?error=exchange");
        }

        return Results.Redirect("/calendars");
    }

    // Must exactly match what's registered in the Google Cloud Console credentials — built
    // from the current request rather than hardcoded so the same code works for local dev
    // and production, as long as both are registered there.
    private static string BuildRedirectUri(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}/oauth/google/callback";
}
