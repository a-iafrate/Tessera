using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Ai.Commands;
using Tessera.Core.Channels;
using Tessera.Core.Conversations;
using Tessera.Core.Reminders;
using Tessera.Core.Resources;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Services;

// Fourth of five domain handler classes to be extracted out of MessageProcessor
// (docs/13-piano-miglioramenti.md, F3). The first of the two riskiest lotti: unlike
// Shopping/Expense/Notes, Reminders carries a cross-turn ConversationState.PendingIntent flow
// ("reminder.llmConfirm") — the model's interpreted due date is read back to the user before
// anything is created (hard rule 14), the same ask-and-replay shape as the space-disambiguation
// question in MessageProcessor itself.
//
// Constructed fresh per message inside MessageProcessor.ProcessAsync, not DI-registered as a
// singleton — same reasoning as the other three: MessageProcessor.channel is a mutable field
// only a per-message instance can safely capture.
public sealed class ReminderHandlers(
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    Func<OnboardingService, ChannelAddress, Guid, string, string, CancellationToken, Task> finalizeReplyAsync)
{
    private sealed record PendingLlmReminder(Guid SpaceId, string Text, DateTimeOffset DueAt, string TimeZoneId);

    // The model's interpreted date is never committed straight away — it's read back to the
    // user first (docs/05-ottimizzazioni.md: "l'unico modo per intercettare l'interpretazione
    // sbagliata prima che diventi un promemoria inutile"), the same ConversationState-backed
    // ask-and-replay mechanism the space disambiguation question uses. Resolves its own
    // services from `scope` (rather than taking them as parameters like the other handlers
    // here) because this runs from HandleLlmFallbackAsync's tool-call switch, where they're
    // already in scope anyway — kept as-is from the original to avoid widening the diff.
    public async Task<string?> HandleLlmReminderAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        JsonElement args, CancellationToken ct)
    {
        var reminderText = args.GetProperty("text").GetString();
        var dueAtText = args.GetProperty("due_at").GetString();
        if (string.IsNullOrWhiteSpace(reminderText) || dueAtText is null
            || !DateTime.TryParse(dueAtText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime))
        {
            return localizer["Errors.NotUnderstood"];
        }

        var timeZoneId = user.TimeZoneId ?? "UTC";
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var dueAt = new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));

        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var payload = new PendingLlmReminder(spaceId, reminderText, dueAt, timeZoneId);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = user.Id };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "reminder.llmConfirm";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var prompt = localizer["Reminders.ConfirmPrompt", reminderText, MessageProcessor.FormatDueAt(dueAt, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "remind.llmconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "remind.llmconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    public async Task HandleLlmReminderConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        // A captured local, not DateTimeOffset.UtcNow inline in the query — identical on SQL
        // Server, but the SQLite provider doesn't translate that specific inline form
        // (docs/13-piano-miglioramenti.md, F4 found the same gap in SpaceResolver).
        var now = DateTimeOffset.UtcNow;
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "reminder.llmConfirm" && s.ExpiresAt > now, ct);
        if (state is null)
        {
            // Expired, or already answered by a previous tap.
            return;
        }

        state.PendingIntent = null;
        await db.SaveChangesAsync(ct);

        if (choice != "yes")
        {
            await channel.SendTextAsync(address, localizer["Reminders.ConfirmCancelled"], ct);
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingLlmReminder>(state.StateJson);
        if (payload is null)
        {
            return;
        }

        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var reminder = await reminders.CreateOnceAsync(payload.SpaceId, user.Id, payload.Text, payload.DueAt, payload.TimeZoneId, ct);

        var undo = scope.ServiceProvider.GetRequiredService<UndoService>();
        await undo.RecordReminderAsync(user.Id, payload.SpaceId, reminder.Id, ct);

        var onboarding = scope.ServiceProvider.GetRequiredService<OnboardingService>();
        var culture = new CultureInfo(user.PreferredCulture);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(payload.TimeZoneId);
        var confirmReply = localizer["Reminders.CreatedOnce", MessageProcessor.FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
        await finalizeReplyAsync(onboarding, address, user.Id, "reminders", confirmReply, ct);
    }

    public async Task HandleReminderCompleteCallbackAsync(
        ReminderService reminders, ChannelAddress address, Guid spaceId, Guid userId, Guid reminderId, CancellationToken ct)
    {
        var reminder = await reminders.CompleteAsync(spaceId, userId, reminderId, ct);
        if (reminder is null)
        {
            // Already completed by a concurrent tap, or gone — the button is stale.
            return;
        }

        await channel.SendTextAsync(address, localizer["Reminders.Completed", reminder.Text], ct);
    }

    public async Task<string?> HandleRemindCommandAsync(
        ReminderService reminders, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, string argsText, CancellationToken ct)
    {
        var command = RemindCommandParser.Parse(argsText);
        if (command is null)
        {
            // Not one of the trivial forms. /remind is a native L1 command and stays fully
            // deterministic on purpose (docs/05-ottimizzazioni.md) — natural language belongs
            // to the "ricordami di/che" intent match instead, which does go to L3.
            return localizer["Reminders.Usage"];
        }

        var timeZoneId = user.TimeZoneId ?? "UTC";
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        switch (command)
        {
            case RemindCommand.ListPending:
                return await HandleRemindListAsync(reminders, address, spaceId, user.Id, timeZone, culture, ct);

            case RemindCommand.CreateOnce once:
            {
                var localTime = once.Time ?? new TimeOnly(9, 0);
                var localDateTime = once.Date.ToDateTime(localTime);
                var dueAt = new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));
                if (dueAt < DateTimeOffset.UtcNow)
                {
                    // No year was given (or it's already past) — assume next year rather
                    // than creating a reminder that is overdue the instant it's created.
                    dueAt = dueAt.AddYears(1);
                }

                var reminder = await reminders.CreateOnceAsync(spaceId, user.Id, once.Text, dueAt, timeZoneId, ct);
                await undo.RecordReminderAsync(user.Id, spaceId, reminder.Id, ct);
                var onceReply = localizer["Reminders.CreatedOnce", MessageProcessor.FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
                await finalizeReplyAsync(onboarding, address, user.Id, "reminders", onceReply, ct);
                return null;
            }

            case RemindCommand.CreateRecurring recurring:
            {
                var todayLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).Date;
                var localDateTime = todayLocal.Add(new TimeOnly(9, 0).ToTimeSpan());
                var firstDueAt = new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));
                if (firstDueAt < DateTimeOffset.UtcNow)
                {
                    firstDueAt = RecurrenceRule.Advance(firstDueAt, recurring.Frequency);
                }

                var reminder = await reminders.CreateRecurringAsync(
                    spaceId, user.Id, recurring.Text, firstDueAt, timeZoneId, recurring.Frequency, ct);
                await undo.RecordReminderAsync(user.Id, spaceId, reminder.Id, ct);
                var recurringReply = localizer["Reminders.CreatedRecurring",
                    MessageProcessor.GetFrequencyDisplayName(recurring.Frequency, localizer), MessageProcessor.FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
                await finalizeReplyAsync(onboarding, address, user.Id, "reminders", recurringReply, ct);
                return null;
            }

            default:
                return null;
        }
    }

    private async Task<string?> HandleRemindListAsync(
        ReminderService reminders, ChannelAddress address, Guid spaceId, Guid userId,
        TimeZoneInfo timeZone, CultureInfo culture, CancellationToken ct)
    {
        var pending = await reminders.GetPendingAsync(spaceId, userId, ct);
        if (pending.Count == 0)
        {
            return localizer["Reminders.ListEmpty"];
        }

        var lines = pending.Select(r =>
            localizer["Reminders.ListItemLine", MessageProcessor.FormatDueAt(r.DueAt, timeZone, culture), r.Text].Value);
        var text = string.Join('\n', lines);

        // One "done" button per reminder — a tap is a callback_query, the same L1 pattern
        // as checking off a shopping list item (docs/05-ottimizzazioni.md).
        var choices = pending.Select(r => new Choice(r.Text, $"remind.complete:{r.Id}")).ToList();
        await channel.SendChoicesAsync(address, text, choices, ct);
        return null;
    }
}
