using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Calendars;
using Tessera.Core.Channels;
using Tessera.Core.Conversations;
using Tessera.Core.Resources;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Services;

// Last of five domain handler classes to be extracted out of MessageProcessor
// (docs/13-piano-miglioramenti.md, F3) — and the largest/riskiest: five distinct
// ConversationState.PendingIntent flows (calendarEvent.llmConfirm/deleteConfirm/moveConfirm,
// plus the read-only query path and CalendarToListSuggestionJob's own callback), all following
// the same read-back-before-committing shape as ReminderHandlers (hard rule 14) — an event
// actually created/deleted/moved on someone's real Google Calendar is far more consequential
// than a reminder, so none of the three mutations ever act off a single LLM call.
//
// None of the calendar-mutation methods below call finalizeReplyAsync themselves (onboarding
// progression / undo button) — that was true of the original MessageProcessor code too:
// calendar mutations reply with a bare confirmation text, no undo offered (deleting/moving on a
// real external calendar isn't something this app can undo anyway) and no onboarding hint
// threaded through. A genuine, pre-existing asymmetry with Shopping/Expense/Notes/Reminders, not
// something to silently "fix" as part of a pure extraction. The delegate is still threaded
// through the constructor, same shape as the other four handlers, because
// HandleCalendarSuggestionCallbackAsync below builds its own local ShoppingHandlers to reuse
// ShowAsync (exactly as MessageProcessor did before this extraction) and that constructor
// requires it — even though ShowAsync itself never actually invokes it.
//
// Constructed fresh per message inside MessageProcessor.ProcessAsync, not DI-registered as a
// singleton — same reasoning as the other four: MessageProcessor.channel is a mutable field
// only a per-message instance can safely capture.
public sealed class CalendarHandlers(
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    Func<OnboardingService, ChannelAddress, Guid, string, string, CancellationToken, Task> finalizeReplyAsync)
{
    // Read-only, so no confirmation round trip is needed — only creating something from an
    // interpreted date goes through that (hard rule 14).
    public async Task<string> HandleCalendarEventsQueryAsync(
        AsyncServiceScope scope, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (!TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "to"), timeZone, out var to))
        {
            return localizer["Errors.NotUnderstood"];
        }

        var events = await calendarQuery.GetEventsAsync(spaceId, user.Id, from, to, ct);
        if (events.Count == 0)
        {
            return localizer["Calendars.EventsEmpty"];
        }

        // A blank line between entries, not a single newline — a long title wraps across
        // several lines in a chat bubble, and without the gap the next entry's date/time reads
        // as a continuation of the previous title instead of a new item.
        return string.Join("\n\n", events.Select(e =>
            localizer["Calendars.EventLine", MessageProcessor.FormatDueAt(e.Start, timeZone, culture), e.Title].Value));
    }

    public async Task<string> HandleCalendarFreeBusyQueryAsync(
        AsyncServiceScope scope, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (!TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "to"), timeZone, out var to))
        {
            return localizer["Errors.NotUnderstood"];
        }

        IReadOnlyList<FreeBusyInterval> busy;
        if (args.TryGetProperty("people", out var peopleProp) && peopleProp.ValueKind == JsonValueKind.Array && peopleProp.GetArrayLength() > 0)
        {
            var names = peopleProp.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();
            var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
            var (targetUserIds, unresolved) = await ResolveMemberNamesAsync(db, spaceId, names, ct);
            if (unresolved.Count > 0)
            {
                return localizer["Calendars.PersonNotFound", string.Join(", ", unresolved)];
            }

            targetUserIds.Add(user.Id);
            busy = await calendarQuery.GetFreeBusyForUsersAsync(spaceId, user.Id, targetUserIds, from, to, ct);
        }
        else
        {
            busy = await calendarQuery.GetFreeBusyAsync(spaceId, user.Id, from, to, ct);
        }

        if (busy.Count == 0)
        {
            return localizer["Calendars.FreeBusyAllFree"];
        }

        // Grouped by day rather than one line per interval — the flat version repeated the
        // date on both ends of every single-day interval ("4 settembre, 09:00 – 4 settembre,
        // 13:00") and gave two same-day slots no visual relationship to each other. A day
        // that's asked about but has nothing booked never appears here — this lists busy time,
        // not a full week's scaffold, so there's nothing to say about a free day.
        var byDay = busy
            .Select(b => (
                Start: TimeZoneInfo.ConvertTime(b.Start, timeZone),
                End: TimeZoneInfo.ConvertTime(b.End, timeZone)))
            .GroupBy(b => b.Start.Date)
            .OrderBy(g => g.Key);

        var lines = byDay.Select(day =>
        {
            var dayLabel = day.Key.ToString("dddd d MMMM", culture);
            var ranges = string.Join(", ", day.Select(b => $"{b.Start.ToString("HH:mm", culture)} – {b.End.ToString("HH:mm", culture)}"));
            return localizer["Calendars.BusyDayLine", dayLabel, ranges].Value;
        });

        return string.Join("\n\n", lines);
    }

    // No existing "typed name -> space member" resolver anywhere else in the codebase — this is
    // the convention: case-insensitive match against DisplayName, falling back to Email (same
    // fallback used everywhere a member's name is rendered), scoped to active memberships only.
    // A name matching more than one member is treated the same as "not found" — disambiguating
    // would need another LLM round trip this architecture doesn't have, so it's simpler and
    // safer to just ask the human to be more specific.
    private static async Task<(List<Guid> Resolved, List<string> Unresolved)> ResolveMemberNamesAsync(
        TesseraDbContext db, Guid spaceId, IReadOnlyList<string> names, CancellationToken ct)
    {
        var members = await db.Memberships
            .Where(m => m.SpaceId == spaceId)
            .Join(db.DomainUsers, m => m.UserId, u => u.Id, (m, u) => u)
            .AsNoTracking()
            .ToListAsync(ct);

        var resolved = new List<Guid>();
        var unresolved = new List<string>();
        foreach (var name in names)
        {
            var matches = members.Where(u => (u.DisplayName ?? u.Email).Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                resolved.Add(matches[0].Id);
            }
            else
            {
                unresolved.Add(name);
            }
        }

        return (resolved, unresolved);
    }

    private sealed record PendingLlmCalendarEvent(Guid SpaceId, string Title, DateTimeOffset Start, DateTimeOffset End);

    // Same read-back-before-committing pattern as ReminderHandlers.HandleLlmReminderAsync (hard
    // rule 14) — an event actually created on the user's real Google Calendar is much more
    // visible (and awkward to silently undo) than a reminder, so misreading the date matters
    // even more here.
    public async Task<string?> HandleLlmCreateCalendarEventAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        if (scope.ServiceProvider.GetService<CalendarQueryService>() is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var title = args.GetProperty("title").GetString();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (string.IsNullOrWhiteSpace(title)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "start"), timeZone, out var start)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "end"), timeZone, out var end))
        {
            return localizer["Errors.NotUnderstood"];
        }

        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var payload = new PendingLlmCalendarEvent(spaceId, title, start, end);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = user.Id };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "calendarEvent.llmConfirm";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var prompt = localizer["Calendars.ConfirmEventPrompt", title, MessageProcessor.FormatDueAt(start, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.llmconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.llmconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    public async Task HandleLlmCalendarEventConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        // A captured local, not DateTimeOffset.UtcNow inline in the query — identical on SQL
        // Server, but the SQLite provider doesn't translate that specific inline form
        // (docs/13-piano-miglioramenti.md, F4 found the same gap in SpaceResolver; F3 lotto 4
        // found the same gap again in ReminderHandlers).
        var now = DateTimeOffset.UtcNow;
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.llmConfirm" && s.ExpiresAt > now, ct);
        if (state is null)
        {
            return;
        }

        state.PendingIntent = null;
        await db.SaveChangesAsync(ct);

        if (choice != "yes")
        {
            await channel.SendTextAsync(address, localizer["Reminders.ConfirmCancelled"], ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingLlmCalendarEvent>(state.StateJson);
        if (payload is null)
        {
            return;
        }

        var calendarQuery = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();
        var created = await calendarQuery.CreateEventAsync(payload.SpaceId, user.Id, payload.Title, payload.Start, payload.End, ct);
        if (created is null)
        {
            await channel.SendTextAsync(address, localizer["Calendars.CreateEventFailed"], ct);
            return;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        var culture = new CultureInfo(user.PreferredCulture);
        var confirmReply = localizer["Calendars.EventCreated", created.Title, MessageProcessor.FormatDueAt(created.Start, timeZone, culture)].Value;
        await channel.SendTextAsync(address, confirmReply, ct);
    }

    private sealed record PendingLlmCalendarEventDelete(Guid SpaceId, Guid ExternalCalendarId, string ProviderEventId, string Title, DateTimeOffset Start);

    // Search-then-confirm, same read-back-before-committing reasoning as creation (hard rule
    // 14) — deleting the wrong event on someone's real calendar is far worse than a mis-typed
    // reminder, so this never deletes straight off a single LLM call.
    public async Task<string?> HandleLlmDeleteCalendarEventAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var searchText = args.GetProperty("search_text").GetString();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (string.IsNullOrWhiteSpace(searchText)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "to"), timeZone, out var to))
        {
            return localizer["Errors.NotUnderstood"];
        }

        var events = await calendarQuery.GetEventsAsync(spaceId, user.Id, from, to, ct);
        var matches = events.Where(e => e.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            return localizer["Calendars.DeleteEventNotFound"];
        }

        if (matches.Count > 1)
        {
            var lines = matches.Select(e => localizer["Calendars.EventLine", MessageProcessor.FormatDueAt(e.Start, timeZone, culture), e.Title].Value);
            return localizer["Calendars.DeleteEventMultipleMatches"] + "\n\n" + string.Join("\n\n", lines);
        }

        var match = matches[0];
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var payload = new PendingLlmCalendarEventDelete(spaceId, match.ExternalCalendarId, match.ProviderEventId, match.Title, match.Start);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = user.Id };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "calendarEvent.deleteConfirm";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var prompt = localizer["Calendars.ConfirmDeleteEventPrompt", match.Title, MessageProcessor.FormatDueAt(match.Start, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.deleteconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.deleteconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    public async Task HandleLlmCalendarEventDeleteConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.deleteConfirm" && s.ExpiresAt > now, ct);
        if (state is null)
        {
            return;
        }

        state.PendingIntent = null;
        await db.SaveChangesAsync(ct);

        if (choice != "yes")
        {
            await channel.SendTextAsync(address, localizer["Reminders.ConfirmCancelled"], ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingLlmCalendarEventDelete>(state.StateJson);
        if (payload is null)
        {
            return;
        }

        var calendarQuery = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();
        var deleted = await calendarQuery.DeleteEventAsync(payload.SpaceId, user.Id, payload.ExternalCalendarId, payload.ProviderEventId, ct);
        var reply = deleted
            ? localizer["Calendars.EventDeleted", payload.Title].Value
            : localizer["Calendars.DeleteEventFailed"].Value;
        await channel.SendTextAsync(address, reply, ct);
    }

    private sealed record PendingLlmCalendarEventMove(
        Guid SpaceId, Guid ExternalCalendarId, string ProviderEventId, string Title, DateTimeOffset NewStart, DateTimeOffset NewEnd);

    // Search-then-confirm, same shape as deletion — the new end time is computed here from the
    // matched event's own duration rather than asked of the model, so a "move to 5pm" request
    // can't accidentally shrink or stretch the event by guessing a default duration.
    public async Task<string?> HandleLlmMoveCalendarEventAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var searchText = args.GetProperty("search_text").GetString();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (string.IsNullOrWhiteSpace(searchText)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "to"), timeZone, out var to)
            || !TryParseLocalDateTime(MessageProcessor.GetOptionalString(args, "new_start"), timeZone, out var newStart))
        {
            return localizer["Errors.NotUnderstood"];
        }

        var events = await calendarQuery.GetEventsAsync(spaceId, user.Id, from, to, ct);
        var matches = events.Where(e => e.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            return localizer["Calendars.DeleteEventNotFound"];
        }

        if (matches.Count > 1)
        {
            var lines = matches.Select(e => localizer["Calendars.EventLine", MessageProcessor.FormatDueAt(e.Start, timeZone, culture), e.Title].Value);
            return localizer["Calendars.DeleteEventMultipleMatches"] + "\n\n" + string.Join("\n\n", lines);
        }

        var match = matches[0];
        var newEnd = newStart + (match.End - match.Start);
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var payload = new PendingLlmCalendarEventMove(spaceId, match.ExternalCalendarId, match.ProviderEventId, match.Title, newStart, newEnd);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = user.Id };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "calendarEvent.moveConfirm";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var prompt = localizer["Calendars.ConfirmMoveEventPrompt", match.Title, MessageProcessor.FormatDueAt(match.Start, timeZone, culture), MessageProcessor.FormatDueAt(newStart, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.moveconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.moveconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    public async Task HandleLlmCalendarEventMoveConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.moveConfirm" && s.ExpiresAt > now, ct);
        if (state is null)
        {
            return;
        }

        state.PendingIntent = null;
        await db.SaveChangesAsync(ct);

        if (choice != "yes")
        {
            await channel.SendTextAsync(address, localizer["Reminders.ConfirmCancelled"], ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingLlmCalendarEventMove>(state.StateJson);
        if (payload is null)
        {
            return;
        }

        var calendarQuery = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();
        var moved = await calendarQuery.MoveEventAsync(payload.SpaceId, user.Id, payload.ExternalCalendarId, payload.ProviderEventId, payload.NewStart, payload.NewEnd, ct);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        var culture = new CultureInfo(user.PreferredCulture);
        var reply = moved is not null
            ? localizer["Calendars.EventMoved", payload.Title, MessageProcessor.FormatDueAt(moved.Start, timeZone, culture)].Value
            : localizer["Calendars.MoveEventFailed"].Value;
        await channel.SendTextAsync(address, reply, ct);
    }

    // "Yes" reuses the exact same shopping-list reply the router's shopping.show/ShowShoppingList
    // path sends — no separate rendering logic, no pending state to store, since which space to
    // show came along in the callback data itself. CalendarToListSuggestionJob's own callback —
    // it doesn't touch CalendarQueryService at all, but it's this job's callback namespace
    // ("calendarSuggest.") that makes it Calendar's to own, not Shopping's.
    public async Task HandleCalendarSuggestionCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string callbackData, CancellationToken ct)
    {
        if (callbackData == "calendarSuggest.no")
        {
            await channel.SendTextAsync(address, localizer["Calendars.ListSuggestionDismissed"], ct);
            return;
        }

        if (!callbackData.StartsWith("calendarSuggest.yes:", StringComparison.Ordinal)
            || !Guid.TryParse(callbackData["calendarSuggest.yes:".Length..], out var spaceId))
        {
            return;
        }

        var shopping = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var shoppingHandlers = new ShoppingHandlers(channel, localizer, finalizeReplyAsync);
        var reply = await shoppingHandlers.ShowAsync(shopping, address, spaceId, user.Id, listName: null, ct);
        if (reply is not null)
        {
            await channel.SendTextAsync(address, reply, ct);
        }
    }

    // Same parsing rule as reminders' due_at (docs/05-ottimizzazioni.md): the model gives a
    // naive local date-time already worked out from the context's current date/time zone, and
    // this attaches the user's actual UTC offset to it.
    private static bool TryParseLocalDateTime(string? text, TimeZoneInfo timeZone, out DateTimeOffset result)
    {
        if (text is null || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime))
        {
            result = default;
            return false;
        }

        result = new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));
        return true;
    }
}
