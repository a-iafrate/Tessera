using System.Security.Claims;
using Microsoft.AspNetCore.Localization;
using Tessera.Data;

namespace Tessera.Web.Endpoints;

// GET rather than a Blazor button click: switching the footer's language before login means
// changing what the *next* server-rendered response looks like, which for Blazor Server means
// a real navigation (a new circuit negotiates its culture once, at connection time, from the
// request — docs/13-piano-miglioramenti.md, B12), not something a SignalR event can change
// mid-circuit.
public static class CultureEndpoints
{
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/set-culture", HandleAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string culture, string? returnUrl, HttpContext context, UserProvisioningService userProvisioning, CancellationToken ct)
    {
        if (culture is not ("en" or "it"))
        {
            return Results.BadRequest();
        }

        // Always set the cookie, even for a signed-in visitor — AuthenticatedUserRequestCultureProvider
        // runs first and wins for them regardless (docs/09-localizzazione.md: User.PreferredCulture
        // is the source of truth), but the cookie is what carries the choice across a sign-out.
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });

        if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } rawUserId && Guid.TryParse(rawUserId, out var userId))
        {
            await userProvisioning.SetPreferredCultureAsync(userId, culture, ct);
        }

        // LocalRedirect, not Redirect: returnUrl is attacker-controlled query input, and this
        // endpoint is anonymous — an open redirect here would be a phishing vector.
        return Results.LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }
}
