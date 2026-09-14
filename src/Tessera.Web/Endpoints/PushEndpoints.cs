using System.Security.Claims;
using Tessera.Core.Abstractions;

namespace Tessera.Web.Endpoints;

// Plain POST JSON endpoints rather than a Blazor method: the subscribe/unsubscribe payload
// comes straight out of the PushManager browser API (push.js), which has no reason to go
// through a SignalR circuit round trip.
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // DisableAntiforgery, like the webhook endpoints — but for a different reason: these
        // are called by plain fetch(), which never has a Blazor antiforgery token to present.
        // What stops a cross-site page from POSTing here on a logged-in user's behalf isn't a
        // token, it's the identity cookie's own SameSite=Lax (Program.cs): a cross-site POST
        // never carries a Lax cookie in the first place, so the request arrives unauthenticated
        // and RequireAuthorization rejects it before it reaches a handler.
        var group = endpoints.MapGroup("/push").RequireAuthorization().DisableAntiforgery();

        group.MapPost("/subscribe", async (
            PushSubscriptionRequest request, ClaimsPrincipal user, IPushSubscriptionRepository subscriptions, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await subscriptions.AddAsync(userId, request.Endpoint, request.P256dh, request.Auth, ct);
            return Results.Ok();
        });

        group.MapPost("/unsubscribe", async (
            PushUnsubscribeRequest request, IPushSubscriptionRepository subscriptions, CancellationToken ct) =>
        {
            await subscriptions.RemoveAsync(request.Endpoint, ct);
            return Results.Ok();
        });

        return endpoints;
    }
}

public sealed record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth);

public sealed record PushUnsubscribeRequest(string Endpoint);
