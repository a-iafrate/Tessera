using System.Security.Cryptography;
using System.Text;

namespace Tessera.Web.Endpoints;

// Telegram sends the secret token set at webhook registration in this header on every
// request — it is the only authentication mechanism for the endpoint (docs/03-integrazioni.md).
public sealed class TelegramSecretTokenFilter(IConfiguration config) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var expected = config["Telegram:WebhookSecret"];
        var received = ctx.HttpContext.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();

        // Constant-time comparison: this is a secret comparison, not a plain equality check.
        if (string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received)))
        {
            return Results.Unauthorized();
        }

        return await next(ctx);
    }
}
