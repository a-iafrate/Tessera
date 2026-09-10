using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Notifications;
using Tessera.Core.Resources;
using Tessera.Data;

namespace Tessera.Web.Services;

// Buffers structured domain events into the recipient's aggregation window instead of sending
// immediately (docs/13-piano-miglioramenti.md, C3) — NotificationAggregationFlushJob renders
// and sends once a window closes, per recipient, in the recipient's own culture, never a
// pre-composed string (docs/09-localizzazione.md, hard rule 8). The actor is excluded from
// their own notification.
public sealed class NotificationService(
    TesseraDbContext db,
    IChannelIdentityRepository identities,
    ActorNameResolver actorNames,
    IChannelRegistry channelRegistry,
    NotificationAggregator aggregator,
    IStringLocalizer<Messages> localizer)
{
    // Channels without both (free proactive sends, an inline keyboard) get a longer window —
    // they're the ones where ten separate messages actually cost something or read as spam
    // (docs/04-costi.md, docs/13 C3), so batching harder there is worth the extra delay.
    private static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LongWindow = TimeSpan.FromMinutes(5);

    public async Task NotifyAsync(ShoppingItemAdded evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        var fact = new ShoppingItemFact(evt.ActorUserId, actorName, evt.ItemText, evt.OriginChatId);
        await BufferForOtherMembersAsync(evt.SpaceId, evt.ActorUserId, nameof(ShoppingItemAdded),
            aggregator.ShoppingItemAdded, fact, ct);
    }

    public async Task NotifyAsync(ShoppingItemChecked evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        var fact = new ShoppingItemFact(evt.ActorUserId, actorName, evt.ItemText, evt.OriginChatId);
        await BufferForOtherMembersAsync(evt.SpaceId, evt.ActorUserId, nameof(ShoppingItemChecked),
            aggregator.ShoppingItemChecked, fact, ct);
    }

    public async Task NotifyAsync(ExpenseRecorded evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        var fact = new ExpenseFact(evt.ActorUserId, actorName, evt.Amount, evt.Currency, evt.CategoryId, evt.OriginChatId);
        await BufferForOtherMembersAsync(evt.SpaceId, evt.ActorUserId, nameof(ExpenseRecorded),
            aggregator.ExpenseRecorded, fact, ct);
    }

    private async Task<string> ResolveActorNameAsync(Guid spaceId, Guid actorUserId, CancellationToken ct) =>
        await actorNames.ResolveAsync(spaceId, actorUserId, ct) ?? localizer["Space.FormerMember"];

    private async Task BufferForOtherMembersAsync<TFact>(
        Guid spaceId, Guid actorUserId, string eventType,
        NotificationAggregationBuffer<TFact> buffer, TFact fact, CancellationToken ct)
    {
        var recipientIds = await db.Memberships
            .Where(m => m.SpaceId == spaceId && m.UserId != actorUserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var recipientId in recipientIds)
        {
            var duration = await WindowDurationForAsync(recipientId, ct);
            buffer.Add(new NotificationWindowKey(spaceId, recipientId, eventType), fact, duration, now);
        }
    }

    private async Task<TimeSpan> WindowDurationForAsync(Guid recipientId, CancellationToken ct)
    {
        var recipientIdentities = await identities.GetForUserAsync(recipientId, ct);
        var hasFastChannel = recipientIdentities.Any(identity =>
            channelRegistry.TryGet(identity.ChannelName) is
            { Capabilities.SupportsProactiveFree: true, Capabilities.SupportsInlineKeyboard: true });

        return hasFastChannel ? ShortWindow : LongWindow;
    }
}
