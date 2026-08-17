using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Calendars;
using Tessera.Core.Channels;
using Tessera.Core.Resources;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Jobs;

// Proactive counterpart to asking "what's on my calendar" — notifies each space member
// individually, shortly before an event they can see starts, even if nobody's chatting with
// the bot at that moment (docs/06-roadmap.md Phase 2, mirrors RemindersDueJob). Access is
// checked implicitly: CalendarQueryService.GetEventsAsync already returns nothing for a
// member without Read-level Calendar access, so no separate permission pass is needed here.
public sealed class CalendarReminderJob(
    IServiceScopeFactory scopeFactory,
    IChannelRegistry channelRegistry,
    IStringLocalizer<Messages> localizer,
    ILogger<CalendarReminderJob> logger) : IScheduledJob
{
    // How far ahead an event has to be to trigger a reminder. Checked every 5 minutes, so an
    // event is seen at least twice inside this window before it starts —
    // CalendarQueryService.WasNotifiedAsync guarantees only the first sighting sends anything.
    private static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(15);

    public string Name => "CalendarReminder";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var calendarQuery = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        var now = DateTimeOffset.UtcNow;
        var windowEnd = now + LeadTime;

        var spaceIds = await db.CalendarSpaceMappings.Select(x => x.SpaceId).Distinct().ToListAsync(ct);
        foreach (var spaceId in spaceIds)
        {
            var memberIds = await db.Memberships.Where(m => m.SpaceId == spaceId).Select(m => m.UserId).ToListAsync(ct);
            foreach (var memberId in memberIds)
            {
                var events = await calendarQuery.GetEventsAsync(spaceId, memberId, now, windowEnd, ct);
                foreach (var e in events)
                {
                    // An all-day event's "start" is midnight — 15 minutes out from that is
                    // meaningless as a heads-up, so it's excluded rather than notified at a
                    // seemingly random moment.
                    if (e.IsAllDay)
                    {
                        continue;
                    }

                    var eventKey = e.IcalUid ?? e.ProviderEventId;
                    if (await calendarQuery.WasNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.Reminder, ct))
                    {
                        continue;
                    }

                    var recipient = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == memberId, ct);
                    if (recipient is null)
                    {
                        await calendarQuery.RecordNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.Reminder, ct);
                        continue;
                    }

                    var culture = new CultureInfo(recipient.PreferredCulture);
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;

                    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(recipient.TimeZoneId ?? "UTC");
                    var text = localizer["Calendars.EventUpcomingNotification", e.Title, MessageProcessor.FormatDueAt(e.Start, timeZone, culture)];

                    var recipientIdentities = await identities.GetForUserAsync(memberId, ct);
                    foreach (var identity in recipientIdentities)
                    {
                        if (channelRegistry.TryGet(identity.ChannelName) is not { } identityChannel
                            || identity.ExternalChatId is not { } chatId)
                        {
                            continue;
                        }

                        try
                        {
                            await identityChannel.SendTextAsync(new ChannelAddress(identity.ChannelName, chatId), text, ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to send calendar reminder for event {EventKey} to {ChannelName}/{ChatId}",
                                eventKey, identity.ChannelName, chatId);
                        }
                    }

                    await calendarQuery.RecordNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.Reminder, ct);
                }
            }
        }
    }
}
