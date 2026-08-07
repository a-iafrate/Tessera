using System.Security.Claims;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Tessera.Data;
using Tessera.Web.Components.Account.Pages;

namespace Microsoft.AspNetCore.Routing;

// These endpoints are required by the Identity Razor components defined under Components/Account.
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/PerformExternalLogin", (
            HttpContext context,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string provider,
            [FromForm] string returnUrl) =>
        {
            IEnumerable<KeyValuePair<string, StringValues>> query = [
                new("ReturnUrl", returnUrl),
                new("Action", ExternalLogin.LoginCallbackAction)];

            var redirectUrl = UriHelper.BuildRelative(
                context.Request.PathBase,
                "/Account/ExternalLogin",
                QueryString.Create(query));

            var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
            return TypedResults.Challenge(properties, [provider]);
        });

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string returnUrl) =>
        {
            await signInManager.SignOutAsync();
            // Trim any leading slash: "~//" resolves to a scheme-relative URL ("//host/..."),
            // which LocalRedirect correctly rejects as non-local.
            return TypedResults.LocalRedirect($"~/{returnUrl.TrimStart('/')}");
        });

        // Plain form POST, not a Blazor component method: sign-out and cookie clearing need a
        // real HTTP response, which an interactive-server circuit can't produce mid-connection
        // (same reason /Logout above is here rather than in a .razor @code block).
        accountGroup.MapPost("/DeleteAccount", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] AccountDeletionService accountDeletion,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await accountDeletion.DeleteAsync(userId, ct);

            var identityUser = await userManager.FindByIdAsync(userId.ToString());
            if (identityUser is not null)
            {
                await userManager.DeleteAsync(identityUser);
            }

            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/");
        });

        return accountGroup;
    }
}
