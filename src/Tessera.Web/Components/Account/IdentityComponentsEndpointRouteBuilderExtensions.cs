using System.Security.Claims;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;
using Tessera.Core.Resources;
using Tessera.Data;
using Tessera.Web.Components.Account;
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

        // Plain form POST rather than a Blazor component method, same reason as /DeleteAccount
        // above: RefreshSignInAsync needs to write a fresh auth cookie (new security stamp),
        // which an interactive-server circuit can't produce mid-connection.
        accountGroup.MapPost("/ChangePassword", async (
            ClaimsPrincipal user,
            HttpContext context,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] IStringLocalizer<Messages> localizer,
            [FromForm] string? currentPassword,
            [FromForm] string newPassword,
            [FromForm] string confirmPassword,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var identityUser = await userManager.FindByIdAsync(userId.ToString());
            if (identityUser is null)
            {
                return TypedResults.LocalRedirect("~/account/change-password");
            }

            if (newPassword != confirmPassword)
            {
                IdentityRedirectManager.SetStatusCookie(context, localizer["ChangePassword.MismatchError"]);
                return TypedResults.LocalRedirect("~/account/change-password");
            }

            // Accounts created via external login only (Google) may never have set a password
            // — AddPasswordAsync, not ChangePasswordAsync, and no current-password check makes
            // sense there since there's nothing to verify it against.
            var hasPassword = await userManager.HasPasswordAsync(identityUser);
            var result = hasPassword
                ? await userManager.ChangePasswordAsync(identityUser, currentPassword ?? "", newPassword)
                : await userManager.AddPasswordAsync(identityUser, newPassword);

            if (!result.Succeeded)
            {
                var message = result.Errors.Any(e => e.Code == "PasswordMismatch")
                    ? localizer["ChangePassword.CurrentPasswordError"]
                    : localizer["ChangePassword.GenericError"];
                IdentityRedirectManager.SetStatusCookie(context, message);
                return TypedResults.LocalRedirect("~/account/change-password");
            }

            // Changing the password rotates the security stamp — without this the existing
            // auth cookie would fail stamp validation on the very next request, signing the
            // user out of the session they just used to make the change.
            await signInManager.RefreshSignInAsync(identityUser);
            IdentityRedirectManager.SetStatusCookie(context, localizer["ChangePassword.Success"]);
            return TypedResults.LocalRedirect("~/account/change-password");
        });

        return accountGroup;
    }
}
