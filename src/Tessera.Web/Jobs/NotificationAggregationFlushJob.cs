using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Expenses;
using Tessera.Core.Notifications;
using Tessera.Core.Resources;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// Drains NotificationAggregator's windows once they close and does the actual send — the
// counterpart to NotificationService, which only buffers (docs/13-piano-miglioramenti.md, C3).
// A window's real duration is 60s-or-longer (decided per recipient when it opens,
// NotificationService.WindowDurationForAsync), so this only needs to run often enough not to
// add much slop on top of that — the 30s below is really just "every SchedulerWorker tick",
// its own tick interval being the actual floor (docs/01-architettura.md).
public sealed class NotificationAggregationFlushJob(
    IServiceScopeFactory scopeFactory,
    NotificationAggregator aggregator,
    IChannelRegistry channelRegistry,
    IStringLocalizer<Messages> localizer,
    ILogger<NotificationAggregationFlushJob> logger) : IScheduledJob
{
    private const int MaxItemsShown = 5;

    public string Name => "NotificationAggregationFlush";

    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await FlushAsync(aggregator.ShoppingItemAdded.TakeReady(now), SendShoppingItemsAddedAsync, ct);
        await FlushAsync(aggregator.ShoppingItemChecked.TakeReady(now), SendShoppingItemsCheckedAsync, ct);
        await FlushAsync(aggregator.ExpenseRecorded.TakeReady(now), SendExpensesRecordedAsync, ct);
    }

    private async Task FlushAsync<TFact>(
        IReadOnlyList<(NotificationWindowKey Key, IReadOnlyList<TFact> Events)> readyWindows,
        Func<IServiceProvider, NotificationWindowKey, IReadOnlyList<TFact>, CancellationToken, Task> send,
        CancellationToken ct)
    {
        if (readyWindows.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        foreach (var (key, events) in readyWindows)
        {
            try
            {
                await send(scope.ServiceProvider, key, events, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to flush aggregated notification window for {SpaceId}/{RecipientUserId}/{EventType}",
                    key.SpaceId, key.RecipientUserId, key.EventType);
            }
        }
    }

    private Task SendShoppingItemsAddedAsync(IServiceProvider sp, NotificationWindowKey key, IReadOnlyList<ShoppingItemFact> events, CancellationToken ct) =>
        DispatchAsync(sp, key, events[^1].OriginChatId,
            () => ComposeShoppingItemsText(events, "Notification.ShoppingItemAdded",
                "Notification.ShoppingItemsAdded", "Notification.ShoppingItemsAddedMultipleActors"),
            ct);

    private Task SendShoppingItemsCheckedAsync(IServiceProvider sp, NotificationWindowKey key, IReadOnlyList<ShoppingItemFact> events, CancellationToken ct) =>
        DispatchAsync(sp, key, events[^1].OriginChatId,
            () => ComposeShoppingItemsText(events, "Notification.ShoppingItemChecked",
                "Notification.ShoppingItemsChecked", "Notification.ShoppingItemsCheckedMultipleActors"),
            ct);

    private async Task SendExpensesRecordedAsync(IServiceProvider sp, NotificationWindowKey key, IReadOnlyList<ExpenseFact> events, CancellationToken ct)
    {
        // Only the single-event case ever shows a category — an aggregate mixing categories
        // would need a per-category breakdown to stay meaningful, which the plan doesn't ask
        // for; the total alone is the useful signal once there's more than one.
        Category? category = null;
        if (events.Count == 1 && events[0].CategoryId is { } categoryId)
        {
            var db = sp.GetRequiredService<TesseraDbContext>();
            category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        }

        await DispatchAsync(sp, key, events[^1].OriginChatId, () => ComposeExpensesText(events, category), ct);
    }

    private string ComposeShoppingItemsText(
        IReadOnlyList<ShoppingItemFact> events, string singleKey, string sameActorKey, string multipleActorsKey)
    {
        if (events.Count == 1)
        {
            return localizer[singleKey, events[0].ActorDisplayName, events[0].ItemText];
        }

        var itemList = FormatList(events.Select(e => e.ItemText));
        return HasSingleActor(events)
            ? localizer[sameActorKey, events[0].ActorDisplayName, events.Count, itemList]
            : localizer[multipleActorsKey, events.Count, itemList];
    }

    private string ComposeExpensesText(IReadOnlyList<ExpenseFact> events, Category? category)
    {
        if (events.Count == 1)
        {
            var single = events[0];
            var formatted = MoneyFormatter.Format(single.Amount, single.Currency, CultureInfo.CurrentUICulture.Name);
            return category is null
                ? localizer["Notification.ExpenseRecorded", single.ActorDisplayName, formatted]
                : localizer["Notification.ExpenseRecordedWithCategory",
                    single.ActorDisplayName, formatted, MessageProcessor.GetCategoryDisplayName(category, localizer)];
        }

        var total = events.Sum(e => e.Amount);
        var formattedTotal = MoneyFormatter.Format(total, events[0].Currency, CultureInfo.CurrentUICulture.Name);
        return HasSingleActor(events)
            ? localizer["Notification.ExpensesRecorded", events[0].ActorDisplayName, events.Count, formattedTotal]
            : localizer["Notification.ExpensesRecordedMultipleActors", events.Count, formattedTotal];
    }

    private static bool HasSingleActor<TFact>(IReadOnlyList<TFact> events) where TFact : IAggregatedNotificationFact =>
        events.Select(e => e.ActorUserId).Distinct().Count() == 1;

    private string FormatList(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count <= MaxItemsShown)
        {
            return string.Join(", ", list);
        }

        return string.Join(", ", list.Take(MaxItemsShown)) + localizer["Notification.AndMore", list.Count - MaxItemsShown];
    }

    private async Task DispatchAsync(
        IServiceProvider sp, NotificationWindowKey key, string? latestOriginChatId, Func<string> composeText, CancellationToken ct)
    {
        var db = sp.GetRequiredService<TesseraDbContext>();
        var identityRepo = sp.GetRequiredService<IChannelIdentityRepository>();

        var recipient = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == key.RecipientUserId, ct);
        if (recipient is null)
        {
            // Deleted between buffering and flush — nothing left to notify.
            return;
        }

        var culture = new CultureInfo(recipient.PreferredCulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var text = composeText();

        var recipientIdentities = await identityRepo.GetForUserAsync(recipient.Id, ct);
        foreach (var identity in recipientIdentities)
        {
            if (channelRegistry.TryGet(identity.ChannelName) is not { } identityChannel
                || identity.ExternalChatId is not { } chatId)
            {
                continue;
            }

            // Email only ever gets the scheduled daily digest (DailyDigestJob), never this
            // real-time fan-out — see ChannelCapabilities.SupportsRealTimeNotifications.
            if (!identityChannel.Capabilities.SupportsRealTimeNotifications)
            {
                continue;
            }

            // The most recent buffered action already happened live in this exact chat (e.g. a
            // shared group both the actor and this recipient are in) — sending it here again
            // would just be an echo.
            if (latestOriginChatId is not null && chatId == latestOriginChatId)
            {
                continue;
            }

            try
            {
                await identityChannel.SendTextAsync(new ChannelAddress(identity.ChannelName, chatId), text, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify {UserId} via {ChannelName}/{ChatId}", recipient.Id, identity.ChannelName, chatId);
            }
        }
    }
}
