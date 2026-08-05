using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Tessera.Core.Channels;
using Tessera.Data;
using Tessera.Web.Services;

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

    // Deduplication happens here, before enqueueing, so a Telegram retry never reaches
    // the queue twice (docs/01-architettura.md pipeline). Responding fast is what keeps
    // Telegram from retrying in the first place — the actual work happens in MessageProcessor.
    private static async Task<IResult> HandleAsync(
        Update update, TesseraDbContext db, MessageQueue queue, CancellationToken ct)
    {
        var inbound = update.ToInbound();
        if (inbound is null)
        {
            return Results.Ok();
        }

        var alreadyProcessed = await db.ProcessedMessages.AnyAsync(
            x => x.ChannelName == inbound.ChannelName && x.ProviderMessageId == inbound.ProviderMessageId, ct);
        if (alreadyProcessed)
        {
            return Results.Ok();
        }

        db.ProcessedMessages.Add(new ProcessedMessage
        {
            ChannelName = inbound.ChannelName,
            ProviderMessageId = inbound.ProviderMessageId,
            ProcessedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await queue.EnqueueAsync(inbound, ct);

        return Results.Ok();
    }
}
