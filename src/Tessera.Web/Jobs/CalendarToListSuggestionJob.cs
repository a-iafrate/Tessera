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

// "Calendario → lista" (docs/06-roadmap.md Fase 4): "sabato cena con i Rossi" proposes adding
// something to the shopping list before the event. Deliberately keyword-based, not LLM-based —
// running an LLM call per calendar event per space per tick would be an uncapped cost sink
// disconnected from anything the user actually asked for (docs/04-costi.md); a title match is
// free and good enough for a nudge, not a hard requirement.
public sealed class CalendarToListSuggestionJob(
    IServiceScopeFactory scopeFactory,
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    ILogger<CalendarToListSuggestionJob> logger) : IScheduledJob
{
    // Enough notice to actually shop before the event, without reaching so far out that the
    // same event lingers in-window (and thus gets re-scanned, though not re-sent thanks to the
    // dedup) for days on end.
    private static readonly TimeSpan LeadWindow = TimeSpan.FromDays(3);

    private static readonly string[] Keywords =
    [
        "cena", "pranzo", "ospiti", "invitati", "aperitivo",
        "dinner", "lunch", "guests", "party",
    ];

    public string Name => "CalendarToListSuggestion";

    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var calendarQuery = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        var now = DateTimeOffset.UtcNow;
        var windowEnd = now + LeadWindow;

        var spaceIds = await db.CalendarSpaceMappings.Select(x => x.SpaceId).Distinct().ToListAsync(ct);
        foreach (var spaceId in spaceIds)
        {
            var memberIds = await db.Memberships.Where(m => m.SpaceId == spaceId).Select(m => m.UserId).ToListAsync(ct);
            foreach (var memberId in memberIds)
            {
                var events = await calendarQuery.GetEventsAsync(spaceId, memberId, now, windowEnd, ct);
                foreach (var e in events)
                {
                    if (e.IsAllDay || !MatchesKeyword(e.Title))
                    {
                        continue;
                    }

                    var eventKey = e.IcalUid ?? e.ProviderEventId;
                    if (await calendarQuery.WasNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.ListSuggestion, ct))
                    {
                        continue;
                    }

                    var recipient = await db.DomainUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == memberId, ct);
                    if (recipient is null)
                    {
                        await calendarQuery.RecordNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.ListSuggestion, ct);
                        continue;
                    }

                    var culture = new CultureInfo(recipient.PreferredCulture);
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;

                    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(recipient.TimeZoneId ?? "UTC");
                    var text = localizer["Calendars.ListSuggestionPrompt", e.Title, MessageProcessor.FormatDueAt(e.Start, timeZone, culture)];
                    var choices = new[]
                    {
                        new Choice(localizer["Reminders.ConfirmYes"].Value, $"calendarSuggest.yes:{spaceId}"),
                        new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarSuggest.no"),
                    };

                    var recipientIdentities = await identities.GetForUserAsync(memberId, ct);
                    foreach (var identity in recipientIdentities)
                    {
                        if (identity.ChannelName != channel.Name || identity.ExternalChatId is not { } chatId)
                        {
                            continue;
                        }

                        try
                        {
                            await channel.SendChoicesAsync(new ChannelAddress(identity.ChannelName, chatId), text, choices, ct);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to send list suggestion for event {EventKey} to {ChannelName}/{ChatId}",
                                eventKey, identity.ChannelName, chatId);
                        }
                    }

                    await calendarQuery.RecordNotifiedAsync(spaceId, memberId, eventKey, e.Start, CalendarNotificationKind.ListSuggestion, ct);
                }
            }
        }
    }

    private static bool MatchesKeyword(string title) =>
        Keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
}
