using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Tessera.Web.Services;

// A one-click, no-login unsubscribe link needs a token that identifies the user without a
// session (docs/13-piano-miglioramenti.md, C1) — ASP.NET Core's own Data Protection API, always
// available without extra configuration, is exactly this: opaque, tamper-proof, and (unlike the
// password-reset token) never expires here on purpose — an unsubscribe link sitting unread in
// an inbox for months should still work whenever it's finally clicked. The only way it stops
// working is a Data Protection key rotation/loss, at which point the toggle on Profile still
// works as a fallback.
public sealed class EmailUnsubscribeTokenService
{
    private readonly IDataProtector protector;

    public EmailUnsubscribeTokenService(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector("Tessera.EmailDigest.Unsubscribe.v1");
    }

    public string CreateToken(Guid userId) => protector.Protect(userId.ToString());

    // Not named TryParse: ASP.NET Core's minimal-API parameter binder looks for a method with
    // exactly that name on *any* handler parameter type — including this one, injected via DI,
    // not bound from the request — and throws at startup over the signature mismatch the moment
    // it finds it.
    public Guid? TryParseToken(string token)
    {
        try
        {
            return Guid.Parse(protector.Unprotect(token));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
