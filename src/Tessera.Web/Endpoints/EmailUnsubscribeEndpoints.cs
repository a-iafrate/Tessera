using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Endpoints;

// GET, not a Blazor button — has to work straight from an email client with no session and no
// circuit, the same reasoning /set-culture (CultureEndpoints) already follows for its own
// no-login link.
public static class EmailUnsubscribeEndpoints
{
    public static IEndpointRouteBuilder MapEmailUnsubscribeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/email/unsubscribe", HandleAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string token, EmailUnsubscribeTokenService tokens, UserProvisioningService userProvisioning, CancellationToken ct)
    {
        if (tokens.TryParseToken(token) is not { } userId)
        {
            return Results.LocalRedirect("/email/unsubscribed?success=false");
        }

        await userProvisioning.SetEmailDigestEnabledAsync(userId, false, ct);
        return Results.LocalRedirect("/email/unsubscribed?success=true");
    }
}
