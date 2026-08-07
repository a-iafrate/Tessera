using System.Security.Claims;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Tessera.Data;

namespace Tessera.Web.Services;

// Reads User.PreferredCulture for authenticated requests — the same property the bot writes
// via /language, so language never diverges between channels (docs/09-localizzazione.md:
// "un solo posto, nessuna divergenza fra canale e web"). Anonymous requests (landing page,
// login) return null and fall through to AcceptLanguageHeaderRequestCultureProvider.
public sealed class AuthenticatedUserRequestCultureProvider : IRequestCultureProvider
{
    public async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var userIdText = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userIdText is null || !Guid.TryParse(userIdText, out var userId))
        {
            return null;
        }

        var db = httpContext.RequestServices.GetRequiredService<TesseraDbContext>();
        var culture = await db.DomainUsers
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PreferredCulture)
            .FirstOrDefaultAsync();

        return culture is null ? null : new ProviderCultureResult(culture);
    }
}
