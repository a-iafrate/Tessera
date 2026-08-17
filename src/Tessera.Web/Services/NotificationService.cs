using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Expenses;
using Tessera.Core.Notifications;
using Tessera.Core.Resources;
using Tessera.Data;

namespace Tessera.Web.Services;

// Renders structured domain events per recipient, in the recipient's own culture — never a
// pre-composed string (docs/09-localizzazione.md, hard rule 8). The actor is excluded from
// their own notification; a recipient whose own chat is the one the action happened in
// (OriginChatId) is skipped there too — they already saw it happen live.
public sealed class NotificationService(
    TesseraDbContext db,
    IChannelIdentityRepository identities,
    ActorNameResolver actorNames,
    IChannelRegistry channelRegistry,
    IStringLocalizer<Messages> localizer,
    ILogger<NotificationService> logger)
{
    public async Task NotifyAsync(ShoppingItemAdded evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        await NotifyOtherMembersAsync(evt.SpaceId, evt.ActorUserId, evt.OriginChatId,
            () => localizer["Notification.ShoppingItemAdded", actorName, evt.ItemText], ct);
    }

    public async Task NotifyAsync(ShoppingItemChecked evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        await NotifyOtherMembersAsync(evt.SpaceId, evt.ActorUserId, evt.OriginChatId,
            () => localizer["Notification.ShoppingItemChecked", actorName, evt.ItemText], ct);
    }

    public async Task NotifyAsync(ExpenseRecorded evt, CancellationToken ct)
    {
        var actorName = await ResolveActorNameAsync(evt.SpaceId, evt.ActorUserId, ct);
        var category = evt.CategoryId is { } categoryId
            ? await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            : null;

        await NotifyOtherMembersAsync(evt.SpaceId, evt.ActorUserId, evt.OriginChatId, () =>
        {
            var formatted = MoneyFormatter.Format(evt.Amount, evt.Currency, CultureInfo.CurrentUICulture.Name);
            return category is null
                ? localizer["Notification.ExpenseRecorded", actorName, formatted]
                : localizer["Notification.ExpenseRecordedWithCategory",
                    actorName, formatted, MessageProcessor.GetCategoryDisplayName(category, localizer)];
        }, ct);
    }

    private async Task<string> ResolveActorNameAsync(Guid spaceId, Guid actorUserId, CancellationToken ct) =>
        await actorNames.ResolveAsync(spaceId, actorUserId, ct) ?? localizer["Space.FormerMember"];

    private async Task NotifyOtherMembersAsync(
        Guid spaceId, Guid actorUserId, string? originChatId, Func<string> composeText, CancellationToken ct)
    {
        var recipients = await db.Memberships
            .Where(m => m.SpaceId == spaceId && m.UserId != actorUserId)
            .Join(db.DomainUsers, m => m.UserId, u => u.Id, (m, u) => u)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var recipient in recipients)
        {
            var culture = new CultureInfo(recipient.PreferredCulture);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            var text = composeText();

            var recipientIdentities = await identities.GetForUserAsync(recipient.Id, ct);
            foreach (var identity in recipientIdentities)
            {
                if (channelRegistry.TryGet(identity.ChannelName) is not { } identityChannel
                    || identity.ExternalChatId is not { } chatId)
                {
                    continue;
                }

                // The action is already visible in this exact chat (e.g. a shared group both
                // the actor and this recipient are in) — a notification there would just be
                // an echo of what they already saw happen live.
                if (originChatId is not null && chatId == originChatId)
                {
                    continue;
                }

                try
                {
                    await identityChannel.SendTextAsync(new ChannelAddress(identity.ChannelName, chatId), text, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to notify {UserId} via {ChannelName}/{ChatId}",
                        recipient.Id, identity.ChannelName, chatId);
                }
            }
        }
    }
}
