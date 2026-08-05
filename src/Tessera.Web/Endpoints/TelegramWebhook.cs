using Telegram.Bot.Types;

namespace Tessera.Web.Endpoints;

public static class TelegramWebhook
{
    public static IEndpointRouteBuilder MapTelegramWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/hooks/telegram", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .AddEndpointFilter<TelegramSecretTokenFilter>();

        return endpoints;
    }

    // Responding fast is what keeps Telegram from retrying in the first place — the
    // actual work happens in MessageProcessor, off the request.
    private static async Task<IResult> HandleAsync(Update update, TelegramUpdateIngestor ingestor, CancellationToken ct)
    {
        await ingestor.IngestAsync(update, ct);
        return Results.Ok();
    }
}
