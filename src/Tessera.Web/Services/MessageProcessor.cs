using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Tessera.Ai.Commands;
using Tessera.Ai.Llm;
using Tessera.Ai.Routing;
using Tessera.Core.Abstractions;
using Tessera.Core.Attachments;
using Tessera.Core.Calendars;
using Tessera.Core.Channels;
using Tessera.Core.Conversations;
using Tessera.Core.Expenses;
using Tessera.Core.Help;
using Tessera.Core.Notes;
using Tessera.Core.Notifications;
using Tessera.Core.Reminders;
using Tessera.Core.Resources;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Services;

// Consumes InboundMessage from the queue. Deduplication already happened at the webhook,
// before enqueueing (docs/01-architettura.md) — this stage does not need to re-check.
public sealed class MessageProcessor(
    MessageQueue queue,
    IServiceScopeFactory scopeFactory,
    IntentRouter router,
    IChannelRegistry channelRegistry,
    PartitionedRateLimiter<string> rateLimiter,
    IStringLocalizer<Messages> localizer,
    ILogger<MessageProcessor> logger,
    TelemetryClient? telemetry = null,
    LlmFallbackClient? llmFallback = null) : BackgroundService
{
    // Resolved once per message at the top of ProcessAsync, then read by every handler below
    // as if it were the single channel this class used to be hardwired to (docs/01-architettura.md).
    // Safe as a mutable field only because the queue is drained strictly sequentially
    // (ExecuteAsync awaits each ProcessAsync fully before reading the next message) — this
    // would need to become a parameter instead if that ever changes.
    private IChannel channel = null!;

    // Command names are canonical English and never shown localized in the menu — these are
    // the router-accepted shortcuts for users who type from habit (docs/09-localizzazione.md,
    // docs/03-integrazioni.md). Kept in sync with Program.cs's setMyCommands registration.
    private static readonly Dictionary<string, string> CommandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/lista"] = "/list",
        ["/spesa"] = "/expense",
        ["/mese"] = "/month",
        ["/collega"] = "/link",
        ["/lingua"] = "/language",
        ["/aiuto"] = "/help",
        ["/nota"] = "/note",
        ["/ricette"] = "/recipes",
        ["/utilizzo"] = "/usage",
    };

    private static string ResolveCommandAlias(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        var firstWord = spaceIndex < 0 ? text : text[..spaceIndex];
        return CommandAliases.TryGetValue(firstWord, out var canonical)
            ? spaceIndex < 0 ? canonical : canonical + text[spaceIndex..]
            : text;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                // A dead end is the worst reply (docs/10-conversazione.md) — even an internal
                // error gets a brief, non-technical apology instead of leaving the chat silent.
                // The correlation id ties that apology back to this exact log entry.
                var correlationId = Guid.NewGuid().ToString("N")[..8];
                logger.LogError(ex,
                    "Failed to process message {ChannelName}/{ProviderMessageId} (correlation {CorrelationId})",
                    message.ChannelName, message.ProviderMessageId, correlationId);

                if (message.ExternalChatId is not null)
                {
                    try
                    {
                        await channel.SendTextAsync(
                            new ChannelAddress(message.ChannelName, message.ExternalChatId),
                            localizer["Errors.Internal", correlationId], stoppingToken);
                    }
                    catch (Exception sendEx)
                    {
                        logger.LogError(sendEx, "Failed to send internal-error reply (correlation {CorrelationId})", correlationId);
                    }
                }
            }
            finally
            {
                // Marked "done" whether ProcessAsync succeeded or threw — a message that
                // failed already got the apology above, and leaving it incomplete would only
                // make PendingMessageRecoveryJob replay the same failure on every future sweep
                // (docs/01-architettura.md). A restart mid-flight, before this runs, is exactly
                // the gap that job exists to close.
                await TryMarkCompletedAsync(message);
            }
        }
    }

    // CancellationToken.None on purpose: this best-effort cleanup should still be attempted
    // during a graceful shutdown (stoppingToken already cancelled) rather than throwing
    // immediately — its own failure is caught and logged here, never allowed to surface as a
    // "message processing failed" error for what was actually a successful run.
    private async Task TryMarkCompletedAsync(InboundMessage message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
            await db.ProcessedMessages
                .Where(x => x.ChannelName == message.ChannelName && x.ProviderMessageId == message.ProviderMessageId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark {ChannelName}/{ProviderMessageId} as completed", message.ChannelName, message.ProviderMessageId);
        }
    }

    private async Task ProcessAsync(InboundMessage message, CancellationToken ct)
    {
        // Resolved first, before anything else can throw — the ExecuteAsync catch block below
        // relies on `channel` already matching this exact message's platform for its own
        // best-effort error reply.
        if (channelRegistry.TryGet(message.ChannelName) is not { } resolvedChannel)
        {
            logger.LogError("No channel registered for '{ChannelName}' — message dropped", message.ChannelName);
            return;
        }

        channel = resolvedChannel;

        // Constructed once per message, not DI-registered (docs/13-piano-miglioramenti.md, F3) —
        // channel is a mutable field just reassigned above, so nothing but a per-message instance
        // could safely capture it. FinalizeUsefulActionReplyAsync stays here for now: it's shared
        // by every domain, not just shopping, so it isn't part of this first extraction batch.
        var shoppingHandlers = new ShoppingHandlers(channel, localizer, FinalizeUsefulActionReplyAsync);
        var expenseHandlers = new ExpenseHandlers(channel, localizer, FinalizeUsefulActionReplyAsync);
        var noteHandlers = new NoteHandlers(channel, localizer, FinalizeUsefulActionReplyAsync);
        var reminderHandlers = new ReminderHandlers(channel, localizer, FinalizeUsefulActionReplyAsync);
        var calendarHandlers = new CalendarHandlers(channel, localizer, FinalizeUsefulActionReplyAsync);

        // Economic safety net, not a feature (docs/07-compliance.md): a loop bug or a bad-
        // faith user must not translate into unlimited DB/LLM cost. Keyed on the raw channel
        // identity, before any DB lookup, so it also caps an unlinked user hammering /start.
        if (message.ExternalUserId is { } externalUserId
            && !rateLimiter.AttemptAcquire($"{message.ChannelName}:{externalUserId}").IsAcquired)
        {
            logger.LogWarning(
                "Rate limit exceeded for {ChannelName} identity {ExternalUserId} — message dropped",
                message.ChannelName, externalUserId);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        if (message.LifecycleEvent is not null)
        {
            await HandleGroupLifecycleEventAsync(scope, message, ct);
            return;
        }

        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();

        // No HTTP context here, so nothing else sets the culture — omitting this produces
        // the silent bug where every reply comes back in English (docs/09-localizzazione.md).
        var user = message.ExternalUserId is null
            ? null
            : await identities.ResolveUserAsync(message.ChannelName, message.ExternalUserId, ct);

        var culture = new CultureInfo(user?.PreferredCulture ?? "en");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (user is null)
        {
            if (message.ExternalUserId is not null && message.Text is { } startText
                && startText.StartsWith("/start ", StringComparison.Ordinal))
            {
                await HandleLinkAsync(scope, message, startText["/start ".Length..].Trim(), ct);
                return;
            }

            // No token to consume yet — point at the console rather than staying silent,
            // since /link is a discoverable menu entry (docs/03-integrazioni.md).
            if (message.ExternalUserId is not null && message.Text is { } linkText
                && ResolveCommandAlias(linkText).StartsWith("/link", StringComparison.OrdinalIgnoreCase))
            {
                var unlinkedAddress = new ChannelAddress(message.ChannelName, message.ExternalChatId);
                await channel.SendTextAsync(unlinkedAddress, localizer["Link.NotLinkedYet"], ct);
                return;
            }

            logger.LogInformation(
                "Unlinked {ChannelName} identity {ExternalUserId} in chat {ExternalChatId} — culture defaulted to {Culture}: {Text}",
                message.ChannelName, message.ExternalUserId, message.ExternalChatId, culture.Name, message.Text);
            return;
        }

        logger.LogInformation(
            "Received {ChannelName} message from {DisplayName} (culture {Culture}): {Text}",
            message.ChannelName, user.DisplayName ?? user.Email, culture.Name, message.Text);

        // Retention at day 7 and day 14 (docs/05-ottimizzazioni.md, docs/06-roadmap.md) is
        // computed from these raw events afterward — one per message is enough, no local
        // pre-aggregation needed.
        telemetry?.TrackEvent("MessageProcessed", new Dictionary<string, string>
        {
            ["UserId"] = user.Id.ToString(),
            ["Culture"] = culture.Name,
            ["Channel"] = message.ChannelName,
        });

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);

        // Italian aliases resolve to the canonical English command before any dispatch below
        // — the menu only ever shows the English name (docs/09-localizzazione.md), but a
        // user typing "/lista" from habit still gets the right handler.
        var text = message.Text is null ? null : ResolveCommandAlias(message.Text);

        // The answer to a disambiguation question (step 5 below) — resolved before anything
        // else needs a space, since resolving it IS what sets the space for the replay.
        if (message.CallbackData is { } spaceChoiceCallback && spaceChoiceCallback.StartsWith("space.choose:", StringComparison.Ordinal))
        {
            await HandleSpaceChoiceCallbackAsync(scope, message, user, ct);
            return;
        }

        // The space for an LLM-proposed reminder is already fixed in the pending payload
        // (it was resolved when the fallback ran) — no need to resolve one again here, same
        // reasoning as the space.choose interception just above.
        if (message.CallbackData is { } remindConfirmCallback && remindConfirmCallback.StartsWith("remind.llmconfirm:", StringComparison.Ordinal))
        {
            await reminderHandlers.HandleLlmReminderConfirmCallbackAsync(scope, address, user, remindConfirmCallback["remind.llmconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the reminder confirmation just above — the space is already fixed
        // in the pending payload.
        if (message.CallbackData is { } calendarConfirmCallback && calendarConfirmCallback.StartsWith("calendarEvent.llmconfirm:", StringComparison.Ordinal))
        {
            await calendarHandlers.HandleLlmCalendarEventConfirmCallbackAsync(scope, address, user, calendarConfirmCallback["calendarEvent.llmconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the two confirmations above.
        if (message.CallbackData is { } calendarDeleteCallback && calendarDeleteCallback.StartsWith("calendarEvent.deleteconfirm:", StringComparison.Ordinal))
        {
            await calendarHandlers.HandleLlmCalendarEventDeleteConfirmCallbackAsync(scope, address, user, calendarDeleteCallback["calendarEvent.deleteconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the three confirmations above.
        if (message.CallbackData is { } calendarMoveCallback && calendarMoveCallback.StartsWith("calendarEvent.moveconfirm:", StringComparison.Ordinal))
        {
            await calendarHandlers.HandleLlmCalendarEventMoveConfirmCallbackAsync(scope, address, user, calendarMoveCallback["calendarEvent.moveconfirm:".Length..], ct);
            return;
        }

        // CalendarToListSuggestionJob's own callback — "yes" carries the space id directly in
        // the callback data (it has no other pending state to fetch it from), "no" needs
        // nothing but the address to reply to.
        if (message.CallbackData is { } calendarSuggestCallback && calendarSuggestCallback.StartsWith("calendarSuggest.", StringComparison.Ordinal))
        {
            await calendarHandlers.HandleCalendarSuggestionCallbackAsync(scope, address, user, calendarSuggestCallback, ct);
            return;
        }

        // "Prenota" after a free-busy answer (docs/13-piano-miglioramenti.md, E5) — same
        // reasoning as the calendar-event confirmations above: the space is already fixed in
        // the pending payload built when the free-busy query ran.
        if (message.CallbackData is { } calendarSlotCallback && calendarSlotCallback.StartsWith("calendarSlot.book:", StringComparison.Ordinal)
            && int.TryParse(calendarSlotCallback["calendarSlot.book:".Length..], out var calendarSlotIndex))
        {
            await calendarHandlers.HandleCalendarSlotBookCallbackAsync(scope, address, user, calendarSlotIndex, ct);
            return;
        }

        // The only ConversationState.PendingIntent flow answered by a plain text reply instead
        // of a button tap (docs/13-piano-miglioramenti.md, E5) — "what should I call it?" after
        // a slot was picked above. Runs on every text message (one indexed lookup when there's
        // nothing pending), not gated behind a callback-data prefix, since free text has none.
        if (text is not null && await calendarHandlers.TryHandlePendingSlotTitleAsync(scope, address, user, text, ct))
        {
            return;
        }

        // A tap on one of the "📎 note title" buttons under a notes list (HandleShowNotesAsync)
        // — the attachment carries its own SpaceId, so no space resolution is needed here.
        if (message.CallbackData is { } noteAttachmentCallback && noteAttachmentCallback.StartsWith("note.showattachment:", StringComparison.Ordinal))
        {
            await noteHandlers.HandleShowNoteAttachmentCallbackAsync(scope, address, user, noteAttachmentCallback["note.showattachment:".Length..], ct);
            return;
        }

        // Undo, onboarding's sample button and the sharing prompt all resolve against
        // LastOperation/the user row directly — none of them need a space resolved first.
        if (message.CallbackData == "undo:tap")
        {
            var undoTapReply = await HandleUndoAsync(scope, user.Id, ct);
            await channel.SendTextAsync(address, undoTapReply, ct);
            return;
        }

        if (message.CallbackData == "onboarding.trysample")
        {
            var sampleReplay = message with
            {
                Text = localizer["Onboarding.SampleAction"].Value,
                CallbackData = null,
                ProviderMessageId = $"replay:{message.ProviderMessageId}",
            };
            await ProcessAsync(sampleReplay, ct);
            return;
        }

        if (message.CallbackData is { } shareCallback && shareCallback.StartsWith("onboarding.share:", StringComparison.Ordinal))
        {
            var shareReply = shareCallback["onboarding.share:".Length..] == "invite"
                ? localizer["Onboarding.ShareInviteInstructions"]
                : localizer["Onboarding.ShareDismissed"];
            await channel.SendTextAsync(address, shareReply, ct);
            return;
        }

        // The fallback space is already fixed in the pending payload — no need to resolve
        // one again here, same reasoning as space.choose above.
        if (message.CallbackData is { } permissionCallback && permissionCallback.StartsWith("permission.fallback:", StringComparison.Ordinal))
        {
            await HandlePermissionFallbackCallbackAsync(scope, message, user, permissionCallback["permission.fallback:".Length..], ct);
            return;
        }

        if (message.CallbackData == "help.show")
        {
            await channel.SendTextAsync(address, HandleHelpCommand(), ct);
            return;
        }

        // /link in a group is the manual remedy for a lost or missed association (e.g. the
        // bot was added while offline, or the auto-link at add-time picked the wrong space)
        // — docs/03-integrazioni.md. In a private chat /link means something else entirely.
        if (text is not null && text.StartsWith("/link", StringComparison.OrdinalIgnoreCase) && message.IsGroupChat)
        {
            if (user.DefaultSpaceId is { } linkSpaceId)
            {
                var linkDb = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
                var linkSpace = await linkDb.Spaces.FirstAsync(s => s.Id == linkSpaceId, ct);

                // Already linked to this exact group (a repeated /link, or the same group
                // reconfirming) doesn't consume another slot — only null -> non-null does
                // (LinkService.GetLinkedBotCountAsync only checks whether GroupChatId is set,
                // not which group it is).
                if (linkSpace.GroupChatId is null)
                {
                    var linkService = scope.ServiceProvider.GetRequiredService<LinkService>();
                    if (!await linkService.CanLinkAnotherBotAsync(linkSpaceId, ct))
                    {
                        await channel.SendTextAsync(address, localizer["Group.LinkLimitReached"], ct);
                        return;
                    }
                }

                linkSpace.GroupChatId = message.ExternalChatId;
                await linkDb.SaveChangesAsync(ct);
            }

            await channel.SendTextAsync(address, localizer["Group.Linked"], ct);
            return;
        }

        // The identity is already linked — a stale/reused /start deep link (e.g. tapped
        // again, or from a different environment sharing the same database), or a bare
        // /link typed out of habit, must not fall through to the intent router and come
        // back as the generic "I didn't get that".
        if (text is not null
            && (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("/link", StringComparison.OrdinalIgnoreCase)))
        {
            await channel.SendTextAsync(address, localizer["Link.AlreadyLinked", user.DisplayName ?? user.Email], ct);
            return;
        }

        // /language and /help touch no resource, so they don't need a space resolved at all.
        if (text is not null && text.StartsWith("/language", StringComparison.OrdinalIgnoreCase))
        {
            var languageReply = await HandleLanguageCommandAsync(scope, user, culture, text["/language".Length..], ct);
            await channel.SendTextAsync(address, languageReply, ct);
            return;
        }

        if (text is not null && text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await channel.SendTextAsync(address, HandleHelpCommand(), ct);
            return;
        }

        // Which native command (if any), and which intent (if natural language) — figured
        // out before resolving the space, since disambiguation is per resource, not per user
        // (docs/02-modello-dati.md). router.TryRoute is pure text matching, no DB, so calling
        // it here costs nothing even for messages that turn out to need a space first.
        var nativeCommand = text is null ? NativeCommand.None : DetectNativeCommand(text);
        IntentMatch? match = null;
        ResourceKind resourceKind;
        AccessLevel requiredLevel;
        var isVoice = message.Media.Count > 0 && message.Media[0].Kind == "voice";

        if (isVoice)
        {
            // Doesn't know yet which resource the transcript will touch — same placeholder the
            // unmatched-text branch below uses, just to resolve a plausible space to charge the
            // transcription against (docs/13-piano-miglioramenti.md, E1). The eventual replay,
            // once the transcript is known, re-resolves the real resource and space from
            // scratch — same two-step shape the L3 fallback already has.
            (resourceKind, requiredLevel) = (ResourceKind.ShoppingList, AccessLevel.Read);
        }
        else if (message.Media.Count > 0 && nativeCommand == NativeCommand.Expense)
        {
            // A photo captioned "/expense" (or its Italian alias) means "read this as a
            // receipt" — Expenses/Write, not Notes (docs/06-roadmap.md Fase 4: "scontrini via
            // vision"). Everything else media-related still means Notes/Write.
            (resourceKind, requiredLevel) = (ResourceKind.Expenses, AccessLevel.Write);
        }
        else if (message.Media.Count > 0)
        {
            // A photo/document always means Notes/Write, whether it ends up creating a new
            // note (captioned) or attaching to the most recent one (uncaptioned) — decided
            // once the space and any disambiguation are resolved, same as every other flow.
            (resourceKind, requiredLevel) = (ResourceKind.Notes, AccessLevel.Write);
        }
        else if (message.CallbackData is { } cd)
        {
            (resourceKind, requiredLevel) = ResourceForCallback(cd);
        }
        else if (nativeCommand != NativeCommand.None)
        {
            (resourceKind, requiredLevel) = ResourceForNativeCommand(nativeCommand);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            match = router.TryRoute(text, culture.Name);
            (resourceKind, requiredLevel) = match is null
                ? (ResourceKind.ShoppingList, AccessLevel.Read)
                : ResourceForIntent(match.Intent);
        }
        else
        {
            return;
        }

        // Router level distribution, overall and per language (docs/05-ottimizzazioni.md): if
        // L3 creeps past 40%, or one language sits almost entirely on L3, that's the signal to
        // improve the router rather than guess at it. A voice message isn't classified here at
        // all — it isn't routed yet, only transcribed — its eventual replay re-enters this same
        // line for the transcript text and gets the real classification then.
        if (!isVoice)
        {
            var routerLevel = message.Media.Count > 0 || message.CallbackData is not null || nativeCommand != NativeCommand.None
                ? "L1"
                : match is not null ? "L2" : "L3";
            telemetry?.TrackEvent($"Router{routerLevel}", new Dictionary<string, string> { ["Culture"] = culture.Name });
        }

        // Touches no resource of its own — the space to act on is whatever LastOperation
        // already recorded, not something to (re-)resolve here (docs/10-conversazione.md).
        if ((text is not null && text.StartsWith("/undo", StringComparison.OrdinalIgnoreCase)) || match?.Intent == "undo")
        {
            var undoCommandReply = await HandleUndoAsync(scope, user.Id, ct);
            await channel.SendTextAsync(address, undoCommandReply, ct);
            return;
        }

        var spaces = scope.ServiceProvider.GetRequiredService<SpaceResolver>();
        var resolution = await spaces.ResolveAsync(user.Id, resourceKind, requiredLevel, text, ct);

        if (resolution.PermissionDeniedSpaceId is { } deniedSpaceId)
        {
            await AskPermissionFallbackAsync(
                scope, address, user.Id, message, deniedSpaceId, resolution, resourceKind, requiredLevel, ct);
            return;
        }

        if (resolution.IsAmbiguous)
        {
            await AskSpaceDisambiguationAsync(scope, address, user.Id, message, resolution.AmbiguousCandidates, ct);
            return;
        }

        if (resolution.SpaceId is not { } spaceId)
        {
            // No accessible space for this resource at all — nothing to do.
            return;
        }

        // Possibly stripped of an explicit "in <space name>" suffix (step 1) — downstream
        // parsing works from this, not the original text.
        text = resolution.RemainingText;

        var shopping = scope.ServiceProvider.GetRequiredService<ShoppingListService>();
        var expenses = scope.ServiceProvider.GetRequiredService<ExpenseService>();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var recurringExpenses = scope.ServiceProvider.GetRequiredService<RecurringExpenseService>();
        var budgets = scope.ServiceProvider.GetRequiredService<BudgetService>();
        var digest = scope.ServiceProvider.GetRequiredService<DigestService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var onboarding = scope.ServiceProvider.GetRequiredService<OnboardingService>();
        var undo = scope.ServiceProvider.GetRequiredService<UndoService>();
        var notes = scope.ServiceProvider.GetRequiredService<NoteService>();
        var usage = scope.ServiceProvider.GetRequiredService<UsageService>();

        if (isVoice)
        {
            await HandleVoiceAsync(scope, usage, address, spaceId, message, message.Media[0], ct);
            return;
        }

        if (message.Media.Count > 0 && nativeCommand == NativeCommand.Expense)
        {
            var receiptReply = await expenseHandlers.HandleReceiptAsync(
                scope, shopping, expenses, budgets, notifications, undo, onboarding, usage, address, spaceId, user, culture, message.Media[0], ct);
            if (receiptReply is not null)
            {
                await channel.SendTextAsync(address, receiptReply, ct);
            }

            return;
        }

        if (message.Media.Count > 0)
        {
            var mediaReply = await noteHandlers.HandleIncomingMediaAsync(scope, notes, address, spaceId, user, text, message.Media[0], ct);
            if (mediaReply is not null)
            {
                await channel.SendTextAsync(address, mediaReply, ct);
            }

            return;
        }

        if (message.CallbackData is { } callbackData)
        {
            // L1 (docs/05-ottimizzazioni.md): an inline-keyboard tap is already a
            // structured action — it never goes through the intent matcher.
            await HandleCallbackAsync(shoppingHandlers, expenseHandlers, reminderHandlers, shopping, expenses, reminders, budgets, notifications, undo, onboarding, address, spaceId, user, culture, callbackData, message.CallbackMessageId, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Native commands (L1), not routed through the intent matcher — their trivial forms
        // are fully deterministic (docs/05-ottimizzazioni.md).
        switch (nativeCommand)
        {
            case NativeCommand.Remind:
            {
                var remindReply = await reminderHandlers.HandleRemindCommandAsync(
                    reminders, undo, onboarding, address, spaceId, user, culture, text["/remind".Length..], ct);
                if (remindReply is not null)
                {
                    await channel.SendTextAsync(address, remindReply, ct);
                }
                return;
            }

            case NativeCommand.Recurring:
            {
                var recurringReply = await expenseHandlers.HandleRecurringCommandAsync(
                    recurringExpenses, spaceId, user, culture, text["/recurring".Length..], ct);
                if (recurringReply is not null)
                {
                    await channel.SendTextAsync(address, recurringReply, ct);
                }
                return;
            }

            case NativeCommand.Budget:
            {
                var budgetReply = await expenseHandlers.HandleBudgetCommandAsync(
                    expenses, budgets, spaceId, user, culture, text["/budget".Length..], ct);
                if (budgetReply is not null)
                {
                    await channel.SendTextAsync(address, budgetReply, ct);
                }
                return;
            }

            case NativeCommand.Digest:
            {
                // /digest triggers the daily digest on demand — the proactive, once-a-day
                // send arrives with IScheduledJob (docs/06-roadmap.md).
                var digestReply = await expenseHandlers.HandleDigestCommandAsync(digest, expenses, spaceId, user, culture, ct);
                await channel.SendTextAsync(address, digestReply, ct);
                return;
            }

            case NativeCommand.Usage:
            {
                var usageReply = await HandleUsageCommandAsync(usage, spaceId, culture, ct);
                await channel.SendTextAsync(address, usageReply, ct);
                return;
            }

            case NativeCommand.List:
            {
                var listReply = await shoppingHandlers.ShowAsync(shopping, address, spaceId, user.Id, listName: null, ct);
                if (listReply is not null)
                {
                    await channel.SendTextAsync(address, listReply, ct);
                }
                return;
            }

            case NativeCommand.Expense:
            {
                var expenseReply = await expenseHandlers.HandleExpenseCommandAsync(
                    expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture, text["/expense".Length..], ct);
                if (expenseReply is not null)
                {
                    await channel.SendTextAsync(address, expenseReply, ct);
                }
                return;
            }

            case NativeCommand.Month:
            {
                var monthReply = await expenseHandlers.HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct);
                await channel.SendTextAsync(address, monthReply, ct);
                return;
            }

            case NativeCommand.Note:
            {
                var noteReply = await noteHandlers.HandleNoteCommandAsync(
                    scope, notes, undo, onboarding, address, spaceId, user.Id, text["/note".Length..], ct);
                if (noteReply is not null)
                {
                    await channel.SendTextAsync(address, noteReply, ct);
                }
                return;
            }

            case NativeCommand.Recipes:
            {
                var preference = text["/recipes".Length..].Trim();
                var recipesReply = await HandleSuggestRecipesAsync(
                    scope, shopping, usage, spaceId, user.Id, culture, preference.Length > 0 ? preference : null, ct);
                if (recipesReply is not null)
                {
                    await channel.SendTextAsync(address, recipesReply, ct);
                }
                return;
            }
        }

        string? reply;
        if (match is not null && match.Intent != "reminders.natural" && match.Intent != "calendar.natural")
        {
            reply = match.Intent switch
            {
                "shopping.add" => await shoppingHandlers.AddAsync(
                    shopping, notifications, undo, onboarding, address, spaceId, user.Id, match.Slots["item"], listName: null, culture, ct),
                "shopping.show" => await shoppingHandlers.ShowAsync(shopping, address, spaceId, user.Id, listName: null, ct),
                "shopping.check" => await shoppingHandlers.CheckAsync(
                    shopping, notifications, undo, address, spaceId, user.Id, match.Slots["item"], listName: null, ct),
                "shopping.remove" => await shoppingHandlers.RemoveAsync(shopping, spaceId, user.Id, match.Slots["item"], listName: null, ct),
                "shopping.clear" => await shoppingHandlers.ClearAsync(shopping, undo, address, spaceId, user.Id, listName: null, ct),
                "expenses.add" => await expenseHandlers.HandleExpenseAddAsync(
                    expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
                    match.Slots["amount"], match.Slots.GetValueOrDefault("category"), match.Slots.GetValueOrDefault("merchant"), ct),
                "expenses.query" => await expenseHandlers.HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct),
                "expenses.query.category" => await expenseHandlers.HandleExpensesQueryByCategoryAsync(
                    expenses, spaceId, user, culture, match.Slots["category"], ct),
                _ => null,
            };
        }
        else
        {
            // Either nothing matched at all, or a reminder/calendar-event attempt was
            // recognized but its date still needs interpreting — all go to L3
            // (docs/05-ottimizzazioni.md).
            reply = await HandleLlmFallbackAsync(
                scope, shoppingHandlers, expenseHandlers, noteHandlers, reminderHandlers, calendarHandlers, shopping, expenses, reminders, notes, budgets, notifications, undo, onboarding, address, spaceId, user, culture, text, ct);
        }

        if (reply is not null)
        {
            await channel.SendTextAsync(address, reply, ct);
        }
    }

    // L3 (docs/05-ottimizzazioni.md): reached when nothing in L1/L2 matched, or a reminder
    // attempt was recognized but its date still needs interpreting. Tool calls dispatch to the
    // same handlers L1/L2 use, so notifications, budget alerts and category assignment stay
    // consistent no matter which router level produced the action.
    private async Task<string?> HandleLlmFallbackAsync(
        AsyncServiceScope scope, ShoppingHandlers shoppingHandlers, ExpenseHandlers expenseHandlers, NoteHandlers noteHandlers, ReminderHandlers reminderHandlers, CalendarHandlers calendarHandlers, ShoppingListService shopping, ExpenseService expenses, ReminderService reminders,
        NoteService notes, BudgetService budgets, NotificationService notifications, UndoService undo, OnboardingService onboarding,
        ChannelAddress address, Guid spaceId, User user, CultureInfo culture, string? text, CancellationToken ct)
    {
        if (llmFallback is null || string.IsNullOrWhiteSpace(text))
        {
            return await SendNotUnderstoodAsync(address, text, culture, ct);
        }

        var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
        if (!await usage.TryRecordL3CallAsync(spaceId, ct))
        {
            // L1/L2 keep working regardless (docs/04-costi.md) — only the LLM call itself,
            // the thing that actually costs money, is gated by the plan's daily allowance.
            return localizer["Usage.LimitExceeded"];
        }

        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == spaceId, ct);
        var recentAction = await undo.GetRecentCorrectableActionAsync(user.Id, ct);

        // Drives which tools LlmTools.Build offers this turn (docs/05-ottimizzazioni.md,
        // "Schema dei tool per contesto") — a member with only Read on Expenses never sees
        // record_expense, and a space with no calendar linked never sees the five calendar
        // tools, regardless of permission. IMembershipRepository is cached (5 min TTL,
        // docs/05), so this is a cache hit on every turn after the first.
        var membershipRepository = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var membership = await membershipRepository.FindAsync(user.Id, spaceId, ct);
        var accessByResource = BuildAccessByResource(membership);
        var hasLinkedCalendar = scope.ServiceProvider.GetService<CalendarQueryService>() is not null
            && await db.CalendarSpaceMappings.AsNoTracking().AnyAsync(m => m.SpaceId == spaceId, ct);

        // Own column, own TTL, independent of the pending-confirmation slot the same row also
        // carries (docs/05-ottimizzazioni.md, "Storico limitato" — A3, docs/13-piano-miglioramenti.md).
        var conversationState = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
        var now = DateTimeOffset.UtcNow;
        var recentExchanges = RecentExchange.Parse(conversationState?.RecentExchangesJson, now);

        var context = new LlmContext(
            culture.Name, user.TimeZoneId ?? "UTC", now, space.Name,
            accessByResource, hasLinkedCalendar, recentExchanges, recentAction?.Description);

        var result = await llmFallback.TryCompleteAsync(text, context, ct);
        if (result is null)
        {
            // The deterministic paths must survive an Azure OpenAI outage
            // (docs/06-roadmap.md) — this is the same honest reply as "not configured". No
            // history entry either: there's nothing the model actually did with this message.
            return await SendNotUnderstoodAsync(address, text, culture, ct);
        }

        await AppendRecentExchangeAsync(db, conversationState, user.Id, text, result, recentExchanges, now, ct);

        if (result.ToolCall is null)
        {
            return result.ReplyText ?? await SendNotUnderstoodAsync(address, text, culture, ct);
        }

        var args = result.ToolCall.Arguments;
        return result.ToolCall.Name switch
        {
            LlmTools.AddShoppingItem => await shoppingHandlers.AddAsync(
                shopping, notifications, undo, onboarding, address, spaceId, user.Id,
                args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), culture, ct),
            LlmTools.CheckShoppingItem => await shoppingHandlers.CheckAsync(
                shopping, notifications, undo, address, spaceId, user.Id,
                args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), ct),
            LlmTools.RemoveShoppingItem => await shoppingHandlers.RemoveAsync(
                shopping, spaceId, user.Id, args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), ct),
            LlmTools.ShowShoppingList => await shoppingHandlers.ShowAsync(shopping, address, spaceId, user.Id, GetOptionalString(args, "list"), ct),
            LlmTools.ClearShoppingList => await shoppingHandlers.ClearAsync(shopping, undo, address, spaceId, user.Id, GetOptionalString(args, "list"), ct),
            LlmTools.ListShoppingLists => await shoppingHandlers.ListListsAsync(shopping, spaceId, user.Id, ct),
            LlmTools.RecordExpense => await expenseHandlers.RecordExpenseAndReplyAsync(
                expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
                args.GetProperty("amount").GetDecimal(),
                args.TryGetProperty("category", out var categoryProp) ? categoryProp.GetString() : null,
                args.TryGetProperty("merchant", out var merchantProp) ? merchantProp.GetString() : null, ct),
            LlmTools.QueryMonthlyExpenses => await expenseHandlers.HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct),
            LlmTools.QueryExpenseHistory => await expenseHandlers.HandleHistoryQueryAsync(expenses, spaceId, user, culture, args, ct),
            LlmTools.QueryPriceHistory => await expenseHandlers.HandlePriceHistoryQueryAsync(expenses, spaceId, user, culture, args, ct),
            LlmTools.QueryPriceByMerchant => await expenseHandlers.HandlePriceByMerchantQueryAsync(expenses, spaceId, user, culture, args, ct),
            LlmTools.SuggestRecipes => await HandleSuggestRecipesAsync(
                scope, shopping, usage, spaceId, user.Id, culture, GetOptionalString(args, "preference"), ct),
            LlmTools.CreateReminder => await reminderHandlers.HandleLlmReminderAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.CreateNote => await noteHandlers.CreateNoteAndReplyAsync(
                notes, undo, onboarding, address, spaceId, user.Id,
                GetOptionalString(args, "title"), args.GetProperty("body").GetString() ?? "", ct),
            LlmTools.ShowNotes => await noteHandlers.HandleShowNotesAsync(scope, address, notes, spaceId, user.Id, ct),
            LlmTools.DeleteNote => await noteHandlers.HandleLlmDeleteNoteAsync(
                scope, notes, spaceId, user.Id, args.GetProperty("search_text").GetString() ?? "", ct),
            LlmTools.QueryCalendarEvents => await calendarHandlers.HandleCalendarEventsQueryAsync(scope, spaceId, user, culture, args, ct),
            LlmTools.QueryCalendarFreeBusy => await calendarHandlers.HandleCalendarFreeBusyQueryAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.CreateCalendarEvent => await calendarHandlers.HandleLlmCreateCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.DeleteCalendarEvent => await calendarHandlers.HandleLlmDeleteCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.MoveCalendarEvent => await calendarHandlers.HandleLlmMoveCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.CorrectLastShoppingItem when recentAction is not null => await shoppingHandlers.CorrectAsync(
                shopping, address, spaceId, user.Id, recentAction.ItemId, args.GetProperty("corrected_text").GetString() ?? "", ct),
            LlmTools.GetHelp => await HandleGetHelpAsync(scope, spaceId, args, ct),
            _ => await SendNotUnderstoodAsync(address, text, culture, ct),
        };
    }

    // The owner's effective level is Admin on every resource regardless of MembershipPermission
    // rows (IAccessPolicy.CanAsync applies the same short-circuit) — a resource with no row at
    // all for a non-owner member is AccessLevel.None, the GetValueOrDefault default in
    // LlmTools.Build. No membership (a caller that got this far without one, which shouldn't
    // normally happen) yields an empty map, which offers no tools at all — the safe failure.
    private static IReadOnlyDictionary<ResourceKind, AccessLevel> BuildAccessByResource(Membership? membership)
    {
        if (membership is null)
        {
            return new Dictionary<ResourceKind, AccessLevel>();
        }

        if (membership.IsOwner)
        {
            return Enum.GetValues<ResourceKind>().ToDictionary(resource => resource, _ => AccessLevel.Admin);
        }

        return membership.Permissions.ToDictionary(p => p.Resource, p => p.Level);
    }

    // Persists this turn as the newest RecentExchange (docs/05-ottimizzazioni.md, "Storico
    // limitato" — A3). existingState/existingExchanges are what HandleLlmFallbackAsync already
    // read for building LlmContext — reused here rather than re-queried, since nothing else
    // could have changed ConversationState for this user in between (the queue is drained
    // strictly sequentially per docs/01-architettura.md, so there's no concurrent writer to
    // race against). Only StateJson/PendingIntent/ExpiresAt are left untouched: those belong to
    // whatever pending-confirmation flow, if any, is independently in progress on the same row.
    private static async Task AppendRecentExchangeAsync(
        TesseraDbContext db, ConversationState? existingState, Guid userId, string userText,
        LlmResult result, IReadOnlyList<RecentExchange> existingExchanges, DateTimeOffset now, CancellationToken ct)
    {
        var state = existingState;
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = userId, ExpiresAt = now };
            db.ConversationStates.Add(state);
        }

        var entry = new RecentExchange(
            userText, result.ToolCall?.Name, result.ToolCall?.Arguments.GetRawText(), now);
        state.RecentExchangesJson = RecentExchange.Serialize(existingExchanges, entry);
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    // The single most useful weekly signal in the product (docs/10-conversazione.md: "leggere
    // le frasi che il bot non ha capito e trasformarle in test") — logged with its own event
    // name so it's easy to grep for review, distinct from the generic message-received line.
    // Always offers a way out via a Help button rather than a bare "I didn't understand"
    // (docs/10-conversazione.md: never a dead end).
    private async Task<string?> SendNotUnderstoodAsync(ChannelAddress address, string? originalText, CultureInfo culture, CancellationToken ct)
    {
        logger.LogInformation("NotUnderstood [{Culture}]: {Text}", culture.Name, originalText);
        telemetry?.TrackEvent("NotUnderstood", new Dictionary<string, string> { ["Culture"] = culture.Name });

        var choices = new[] { new Choice(localizer["Commands.Help.Description"].Value, "help.show") };
        await channel.SendChoicesAsync(address, localizer["Errors.NotUnderstood"], choices, ct);
        return null;
    }

    internal static string? GetOptionalString(JsonElement args, string propertyName) =>
        args.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    // Records the action's undo button on the confirmation, and — before sending — folds in
    // whichever onboarding nudge (if any) applies: a discovery hint appended to the same
    // message, or the one-time sharing prompt as a separate follow-up
    // (docs/10-conversazione.md: one novelty at a time).
    private async Task FinalizeUsefulActionReplyAsync(
        OnboardingService onboarding, ChannelAddress address, Guid userId, string featureKey, string baseReply, CancellationToken ct)
    {
        var count = await onboarding.RecordUsefulActionAsync(userId, ct);

        if (count == 3 && await onboarding.TryShowSharingPromptOnceAsync(userId, ct))
        {
            await SendWithUndoAsync(address, baseReply, ct);
            var shareChoices = new[]
            {
                new Choice(localizer["Onboarding.ShareInvite"].Value, "onboarding.share:invite"),
                new Choice(localizer["Onboarding.ShareLater"].Value, "onboarding.share:later"),
            };
            await channel.SendChoicesAsync(address, localizer["Onboarding.SharePrompt"], shareChoices, ct);
            return;
        }

        var hintKey = await onboarding.NextDiscoveryHintKeyAsync(userId, featureKey, ct);
        var finalReply = hintKey is null ? baseReply : $"{baseReply}\n\n{DescribeHint(hintKey)}";
        await SendWithUndoAsync(address, finalReply, ct);
    }

    private string DescribeHint(string hintKey) => hintKey switch
    {
        "shopping" => localizer["Onboarding.HintShopping"],
        "expenses" => localizer["Onboarding.HintExpenses"],
        "reminders" => localizer["Onboarding.HintReminders"],
        "notes" => localizer["Onboarding.HintNotes"],
        _ => "",
    };

    private async Task SendWithUndoAsync(ChannelAddress address, string text, CancellationToken ct)
    {
        var choices = new[] { new Choice(localizer["Undo.Button"].Value, "undo:tap") };
        await channel.SendChoicesAsync(address, text, choices, ct);
    }

    private async Task<string> HandleUndoAsync(AsyncServiceScope scope, Guid userId, CancellationToken ct)
    {
        var undo = scope.ServiceProvider.GetRequiredService<UndoService>();
        var outcome = await undo.TryUndoLastAsync(userId, ct);
        return outcome switch
        {
            UndoSucceeded s => DescribeUndone(s.OperationType),
            UndoConflict => localizer["Undo.Conflict"],
            _ => localizer["Undo.Nothing"],
        };
    }

    private string DescribeUndone(string operationType) => operationType switch
    {
        "shopping.add" => localizer["Undo.ShoppingAdd"],
        "shopping.check" => localizer["Undo.ShoppingCheck"],
        "shopping.clear" => localizer["Undo.ShoppingClear"],
        "expense.record" => localizer["Undo.ExpenseRecord"],
        "reminder.create" => localizer["Undo.ReminderCreate"],
        "note.create" => localizer["Undo.NoteCreate"],
        _ => localizer["Undo.Generic"],
    };

    private enum NativeCommand
    {
        None,
        Remind,
        Recurring,
        Budget,
        Digest,
        List,
        Expense,
        Month,
        Note,
        Usage,
        Recipes,
    }

    private static NativeCommand DetectNativeCommand(string text) => text switch
    {
        _ when text.StartsWith("/remind", StringComparison.OrdinalIgnoreCase) => NativeCommand.Remind,
        _ when text.StartsWith("/recurring", StringComparison.OrdinalIgnoreCase) => NativeCommand.Recurring,
        _ when text.StartsWith("/budget", StringComparison.OrdinalIgnoreCase) => NativeCommand.Budget,
        _ when text.StartsWith("/digest", StringComparison.OrdinalIgnoreCase) => NativeCommand.Digest,
        _ when text.StartsWith("/list", StringComparison.OrdinalIgnoreCase) => NativeCommand.List,
        _ when text.StartsWith("/expense", StringComparison.OrdinalIgnoreCase) => NativeCommand.Expense,
        _ when text.StartsWith("/month", StringComparison.OrdinalIgnoreCase) => NativeCommand.Month,
        _ when text.StartsWith("/note", StringComparison.OrdinalIgnoreCase) => NativeCommand.Note,
        _ when text.StartsWith("/usage", StringComparison.OrdinalIgnoreCase) => NativeCommand.Usage,
        _ when text.StartsWith("/recipes", StringComparison.OrdinalIgnoreCase) => NativeCommand.Recipes,
        _ => NativeCommand.None,
    };

    // Read, even for commands that can also write (/remind, /recurring, /budget bare vs.
    // with args) — this only decides *which space* is a candidate for disambiguation, not
    // whether the action is authorized; each Service's own EnsureAccessAsync still enforces
    // the real Write requirement for a mutation and rejects it if the resolved space lacks
    // it. Asking for Write here would wrongly exclude a Read-only space from candidates for
    // what might turn out to be a read-only bare command (docs/02-modello-dati.md).
    private static (ResourceKind, AccessLevel) ResourceForNativeCommand(NativeCommand command) => command switch
    {
        NativeCommand.Remind => (ResourceKind.Reminders, AccessLevel.Read),
        NativeCommand.Recurring or NativeCommand.Budget => (ResourceKind.Expenses, AccessLevel.Read),
        // /expense always writes — no read-only variant, so the stronger requirement is
        // exactly right here.
        NativeCommand.Expense => (ResourceKind.Expenses, AccessLevel.Write),
        // /digest spans three resources; ShoppingList is an arbitrary but reasonable anchor
        // (docs/02-modello-dati.md doesn't cover multi-resource disambiguation).
        NativeCommand.Digest => (ResourceKind.ShoppingList, AccessLevel.Read),
        NativeCommand.List => (ResourceKind.ShoppingList, AccessLevel.Read),
        NativeCommand.Month => (ResourceKind.Expenses, AccessLevel.Read),
        NativeCommand.Note => (ResourceKind.Notes, AccessLevel.Read),
        NativeCommand.Recipes => (ResourceKind.ShoppingList, AccessLevel.Read),
        _ => (ResourceKind.ShoppingList, AccessLevel.Read),
    };

    private static (ResourceKind, AccessLevel) ResourceForIntent(string intent) => intent switch
    {
        "shopping.add" or "shopping.check" or "shopping.remove" or "shopping.clear" => (ResourceKind.ShoppingList, AccessLevel.Write),
        "shopping.show" => (ResourceKind.ShoppingList, AccessLevel.Read),
        "expenses.add" => (ResourceKind.Expenses, AccessLevel.Write),
        "expenses.query" or "expenses.query.category" => (ResourceKind.Expenses, AccessLevel.Read),
        // Read, not Write: this only picks which space is a candidate, same reasoning as
        // ResourceForNativeCommand — the actual Write requirement for creating an event is
        // enforced later, in CalendarQueryService.CreateEventAsync. Without this case,
        // "calendar.natural" fell into the default ShoppingList anchor below, resolving to
        // whichever space has the widest shopping-list access rather than the one where a
        // calendar is actually mapped.
        "calendar.natural" => (ResourceKind.Calendar, AccessLevel.Read),
        _ => (ResourceKind.ShoppingList, AccessLevel.Read),
    };

    private static (ResourceKind, AccessLevel) ResourceForCallback(string callbackData) => callbackData.Split(':')[0] switch
    {
        "shopping.check" => (ResourceKind.ShoppingList, AccessLevel.Write),
        "expcat" or "expconfirm" => (ResourceKind.Expenses, AccessLevel.Write),
        "remind.complete" or "warranty" => (ResourceKind.Reminders, AccessLevel.Write),
        _ => (ResourceKind.ShoppingList, AccessLevel.Read),
    };

    private sealed record PendingSpaceChoice(IReadOnlyList<Guid> CandidateSpaceIds, string? OriginalText, string? OriginalCallbackData);

    // Step 5 of the precedence chain: ask, and remember the answer for the TTL window so it
    // isn't asked again on every message (docs/02-modello-dati.md).
    private async Task AskSpaceDisambiguationAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid userId, InboundMessage message,
        IReadOnlyList<Guid> candidateSpaceIds, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var candidateSpaces = await db.Spaces
            .Where(s => candidateSpaceIds.Contains(s.Id))
            .OrderBy(s => s.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        var payload = new PendingSpaceChoice(candidateSpaces.Select(s => s.Id).ToList(), message.Text, message.CallbackData);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = userId };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "space.choice";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var choices = candidateSpaces.Select((s, index) => new Choice(s.Name, $"space.choose:{index}")).ToList();
        await channel.SendChoicesAsync(address, localizer["Space.WhichOne"], choices, ct);
    }

    // Sets the answer from step 5, then replays the original action — now unambiguous, since
    // ConversationState.ActiveSpaceId (step 2) resolves it this time.
    private async Task HandleSpaceChoiceCallbackAsync(
        AsyncServiceScope scope, InboundMessage message, User user, CancellationToken ct)
    {
        if (!int.TryParse(message.CallbackData!["space.choose:".Length..], out var index))
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "space.choice" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (state is null)
        {
            // Expired, or already answered by a previous tap — the button is stale.
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingSpaceChoice>(state.StateJson);
        if (payload is null || index < 0 || index >= payload.CandidateSpaceIds.Count)
        {
            return;
        }

        var spaces = scope.ServiceProvider.GetRequiredService<SpaceResolver>();
        await spaces.SetActiveSpaceAsync(user.Id, payload.CandidateSpaceIds[index], ct);

        var replay = message with
        {
            Text = payload.OriginalText,
            CallbackData = payload.OriginalCallbackData,
            ProviderMessageId = $"replay:{message.ProviderMessageId}",
        };
        await ProcessAsync(replay, ct);
    }

    private sealed record PendingPermissionFallback(Guid FallbackSpaceId, string? OriginalText, string? OriginalCallbackData);

    // The user named a real space they belong to, but it doesn't have the permission this
    // needs — name the space and the missing permission, and offer the plausible alternative
    // instead of silently acting somewhere else (docs/10-conversazione.md).
    private async Task AskPermissionFallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid userId, InboundMessage message, Guid deniedSpaceId,
        SpaceResolution resolution, ResourceKind resourceKind, AccessLevel requiredLevel, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var deniedSpace = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == deniedSpaceId, ct);

        var memberships = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var membership = await memberships.FindAsync(userId, deniedSpaceId, ct);
        var currentLevel = membership?.Permissions.FirstOrDefault(p => p.Resource == resourceKind)?.Level ?? AccessLevel.None;

        if (resolution.SpaceId is not { } fallbackSpaceId)
        {
            // No plausible alternative to offer — practically unreachable, since the personal
            // space always qualifies, but state the problem rather than guess if it happens.
            await channel.SendTextAsync(address, localizer["Permission.DeniedNoAlternative",
                deniedSpace.Name, ResourceDisplayName(resourceKind), LevelDisplayName(currentLevel)], ct);
            return;
        }

        var fallbackSpace = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == fallbackSpaceId, ct);

        var payload = new PendingPermissionFallback(fallbackSpaceId, resolution.RemainingText, message.CallbackData);
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is null)
        {
            state = new ConversationState { Id = Guid.NewGuid(), UserId = userId };
            db.ConversationStates.Add(state);
        }

        state.PendingIntent = "permission.fallback";
        state.StateJson = JsonSerializer.Serialize(payload);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        var prompt = localizer["Permission.DeniedWithAlternative",
            deniedSpace.Name, ResourceDisplayName(resourceKind), LevelDisplayName(currentLevel), fallbackSpace.Name];
        var choices = new[]
        {
            new Choice(localizer["Permission.UseAlternative", fallbackSpace.Name].Value, "permission.fallback:yes"),
            new Choice(localizer["Permission.Cancel"].Value, "permission.fallback:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
    }

    private async Task HandlePermissionFallbackCallbackAsync(
        AsyncServiceScope scope, InboundMessage message, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "permission.fallback" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (state is null)
        {
            // Expired, or already answered by a previous tap — the button is stale.
            return;
        }

        state.PendingIntent = null;
        await db.SaveChangesAsync(ct);

        if (choice != "yes")
        {
            return;
        }

        var payload = JsonSerializer.Deserialize<PendingPermissionFallback>(state.StateJson);
        if (payload is null)
        {
            return;
        }

        var spaces = scope.ServiceProvider.GetRequiredService<SpaceResolver>();
        await spaces.SetActiveSpaceAsync(user.Id, payload.FallbackSpaceId, ct);

        var replay = message with
        {
            Text = payload.OriginalText,
            CallbackData = payload.OriginalCallbackData,
            ProviderMessageId = $"replay:{message.ProviderMessageId}",
        };
        await ProcessAsync(replay, ct);
    }

    private string ResourceDisplayName(ResourceKind resource) => resource switch
    {
        ResourceKind.ShoppingList => localizer["ResourceKind.ShoppingList"],
        ResourceKind.Expenses => localizer["ResourceKind.Expenses"],
        ResourceKind.Reminders => localizer["ResourceKind.Reminders"],
        ResourceKind.Calendar => localizer["ResourceKind.Calendar"],
        ResourceKind.Notes => localizer["ResourceKind.Notes"],
        _ => resource.ToString(),
    };

    private string LevelDisplayName(AccessLevel level) => level switch
    {
        AccessLevel.None => localizer["AccessLevel.None"],
        AccessLevel.Availability => localizer["AccessLevel.Availability"],
        AccessLevel.Read => localizer["AccessLevel.Read"],
        AccessLevel.Write => localizer["AccessLevel.Write"],
        AccessLevel.Admin => localizer["AccessLevel.Admin"],
        _ => localizer["AccessLevel.None"],
    };

    private async Task HandleCallbackAsync(
        ShoppingHandlers shoppingHandlers, ExpenseHandlers expenseHandlers, ReminderHandlers reminderHandlers, ShoppingListService shopping, ExpenseService expenses, ReminderService reminders, BudgetService budgets,
        NotificationService notifications, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, string callbackData, string? callbackMessageId, CancellationToken ct)
    {
        var parts = callbackData.Split(':');

        if (parts.Length == 2 && parts[0] == "shopping.check" && Guid.TryParse(parts[1], out var itemId))
        {
            await shoppingHandlers.HandleCheckCallbackAsync(shopping, notifications, undo, address, spaceId, user.Id, itemId, callbackMessageId, ct);
            return;
        }

        if (parts.Length == 2 && parts[0] == "shopping.remove" && Guid.TryParse(parts[1], out var removeItemId))
        {
            await shoppingHandlers.HandleRemoveCallbackAsync(shopping, address, spaceId, user.Id, removeItemId, callbackMessageId, ct);
            return;
        }

        if (parts.Length == 3 && parts[0] == "expcat"
            && Guid.TryParse(parts[1], out var expenseId) && int.TryParse(parts[2], out var categoryIndex))
        {
            await expenseHandlers.HandleExpenseCategorizeCallbackAsync(expenses, address, spaceId, expenseId, categoryIndex, ct);
            return;
        }

        if (parts.Length == 3 && parts[0] == "expconfirm" && Guid.TryParse(parts[1], out var pendingId))
        {
            await expenseHandlers.HandleExpenseConfirmCallbackAsync(
                expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture, pendingId, parts[2], ct);
            return;
        }

        if (parts.Length == 2 && parts[0] == "remind.complete" && Guid.TryParse(parts[1], out var reminderId))
        {
            await reminderHandlers.HandleReminderCompleteCallbackAsync(reminders, address, spaceId, user.Id, reminderId, ct);
            return;
        }

        if (parts.Length == 3 && parts[0] == "warranty" && Guid.TryParse(parts[1], out var warrantyLineId))
        {
            await expenseHandlers.HandleWarrantyReminderCallbackAsync(expenses, reminders, address, spaceId, user, culture, warrantyLineId, parts[2], ct);
        }
    }

    private async Task HandleLinkAsync(AsyncServiceScope scope, InboundMessage message, string token, CancellationToken ct)
    {
        var linkService = scope.ServiceProvider.GetRequiredService<LinkService>();
        var linkedUser = await linkService.ConsumeTokenAsync(
            token, message.ChannelName, message.ExternalUserId!, message.ExternalChatId, ct);

        var culture = new CultureInfo(linkedUser?.PreferredCulture ?? "en");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        logger.LogInformation("Link attempt for {ChannelName} identity {ExternalUserId}: {Result}",
            message.ChannelName, message.ExternalUserId, linkedUser is null ? "invalid/expired" : "success");

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);

        if (linkedUser is null)
        {
            await channel.SendTextAsync(address, localizer["Link.Invalid"], ct);
            return;
        }

        // First value before configuration (docs/10-conversazione.md): someone who's never
        // done anything useful yet gets the onboarding welcome with a one-tap sample action
        // instead of the bare "linked as" line — a returning/relinking user already knows how
        // this works.
        if (linkedUser.UsefulActionCount == 0)
        {
            var choices = new[] { new Choice(localizer["Onboarding.SampleButtonLabel"].Value, "onboarding.trysample") };
            await channel.SendChoicesAsync(address, localizer["Onboarding.Welcome"], choices, ct);
            return;
        }

        await channel.SendTextAsync(address, localizer["Link.Success", linkedUser.DisplayName ?? linkedUser.Email], ct);
    }

    private async Task HandleGroupLifecycleEventAsync(AsyncServiceScope scope, InboundMessage message, CancellationToken ct)
    {
        var evt = message.LifecycleEvent!;
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();

        switch (evt.Type)
        {
            case GroupLifecycleEventType.ChatMigrated:
                await RemapGroupChatAsync(db, evt.OldChatId!, message.ExternalChatId, ct);
                break;

            case GroupLifecycleEventType.BotRemoved:
                // Zeroes GroupChatId only — the space and its data survive being removed
                // and re-added (docs/03-integrazioni.md).
                await ClearGroupChatAsync(db, message.ExternalChatId, ct);
                break;

            case GroupLifecycleEventType.BotAdded:
                await HandleBotAddedToGroupAsync(scope, db, message, ct);
                break;
        }
    }

    // Idempotent: both migration forms (docs/03-integrazioni.md) can arrive for the same
    // event, and a re-delivery must not fail or duplicate the remap.
    private static async Task RemapGroupChatAsync(TesseraDbContext db, string oldChatId, string newChatId, CancellationToken ct)
    {
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.GroupChatId == oldChatId, ct);
        if (space is null || space.GroupChatId == newChatId)
        {
            return;
        }

        space.PreviousGroupChatId = space.GroupChatId;
        space.GroupChatId = newChatId;
        await db.SaveChangesAsync(ct);
    }

    private static async Task ClearGroupChatAsync(TesseraDbContext db, string chatId, CancellationToken ct)
    {
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.GroupChatId == chatId, ct);
        if (space is null)
        {
            return;
        }

        space.GroupChatId = null;
        await db.SaveChangesAsync(ct);
    }

    // Auto-associates the adder's own personal space with the group, matching docs/10's
    // example ("l'assistente di Alessio") — no disambiguation UI exists yet, so this is the
    // deterministic default. /link in the group (a later checklist item) is the manual
    // remedy once the mapping is lost or wrong.
    private async Task HandleBotAddedToGroupAsync(
        AsyncServiceScope scope, TesseraDbContext db, InboundMessage message, CancellationToken ct)
    {
        var identities = scope.ServiceProvider.GetRequiredService<IChannelIdentityRepository>();
        var adder = message.ExternalUserId is null
            ? null
            : await identities.ResolveUserAsync(message.ChannelName, message.ExternalUserId, ct);

        var culture = new CultureInfo(adder?.PreferredCulture ?? "en");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);

        if (adder?.DefaultSpaceId is not { } spaceId)
        {
            await channel.SendTextAsync(address, localizer["Group.WelcomeUnlinked"], ct);
            return;
        }

        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);

        if (space.GroupChatId is null)
        {
            var linkService = scope.ServiceProvider.GetRequiredService<LinkService>();
            if (!await linkService.CanLinkAnotherBotAsync(spaceId, ct))
            {
                await channel.SendTextAsync(address, localizer["Group.LinkLimitReached"], ct);
                return;
            }
        }

        space.GroupChatId = message.ExternalChatId;
        await db.SaveChangesAsync(ct);

        await channel.SendTextAsync(address, localizer["Group.Welcome", adder.DisplayName ?? adder.Email], ct);
    }

    // "Ricette e suggerimenti dalla lista" (docs/06-roadmap.md) — reads whatever is currently
    // on the default shopping list (bought or not: both count as "what the household has or
    // is about to have") and asks the model for a couple of recipe ideas. No plan gate like
    // receipts: this is an ordinary text completion, the same cost class as any other L3
    // turn, so it only spends the space's normal daily allowance. The native /recipes command
    // spends one call (the generation itself); natural language spends two, since the router's
    // own decision that this is a suggest_recipes request is a first L3 call.
    private async Task<string?> HandleSuggestRecipesAsync(
        AsyncServiceScope scope, ShoppingListService shopping, UsageService usage, Guid spaceId, Guid userId,
        CultureInfo culture, string? preference, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, listName: null, ct);
        if (items.Count == 0)
        {
            return localizer["Recipes.ListEmpty"];
        }

        var recipes = scope.ServiceProvider.GetService<RecipeSuggestionClient>();
        if (recipes is null)
        {
            return localizer["Recipes.NotConfigured"];
        }

        if (!await usage.TryRecordL3CallAsync(spaceId, ct))
        {
            return localizer["Usage.LimitExceeded"];
        }

        var suggestion = await recipes.SuggestAsync(items.Select(i => i.RawText).ToList(), preference, culture.Name, ct);
        return suggestion ?? localizer["Recipes.NotAvailable"];
    }

    // "How do I..." questions about the product itself, answered by static per-topic text
    // (HelpTopics — shared with the web console's /help page) rather than a free-form LLM
    // answer, so the wording never drifts and never invents a feature that doesn't exist.
    // Never a dead end (docs/10-conversazione.md): an unrecognized topic still gets a helpful
    // reply, not a bare "I didn't understand".
    private async Task<string> HandleGetHelpAsync(AsyncServiceScope scope, Guid spaceId, JsonElement args, CancellationToken ct)
    {
        if (HelpTopicCodes.FromCode(GetOptionalString(args, "topic")) is not { } topic)
        {
            return localizer["Help.TopicNotUnderstood"];
        }

        var answer = HelpTopics.GetAnswer(topic, localizer);
        if (HelpTopics.GetRoutePattern(topic) is not { } routePattern)
        {
            return answer;
        }

        // App:BaseUrl is the same config key DailyDigestJob already uses to build absolute
        // links (unsubscribe) — no IUrlHelper/route-name helper exists in backend code, so this
        // is the second use of that same ad hoc pattern, not a new one.
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        if (configuration["App:BaseUrl"]?.TrimEnd('/') is not { } baseUrl)
        {
            return answer;
        }

        var link = baseUrl + string.Format(CultureInfo.InvariantCulture, routePattern, spaceId);
        return $"{answer}\n\n{link}";
    }

    // A cost limit, not a protocol one — Telegram itself allows voice messages up to ~60
    // minutes; this is about not paying to transcribe someone's pocket-recorded meeting
    // (docs/13-piano-miglioramenti.md, E1).
    private const int MaxVoiceSeconds = 60;

    // Transcribes, then replays the transcript through ProcessAsync from the top — the same
    // router a typed message goes through, not a shortcut to L3 (docs/13-piano-miglioramenti.md,
    // E1). Everything about "which space, which resource, which permission" is decided fresh by
    // that replay; this method's own job is only "is this worth paying to transcribe".
    private async Task HandleVoiceAsync(
        AsyncServiceScope scope, UsageService usage, ChannelAddress address, Guid spaceId,
        InboundMessage message, InboundMedia media, CancellationToken ct)
    {
        var transcription = scope.ServiceProvider.GetService<VoiceTranscriptionClient>();
        if (transcription is null)
        {
            await channel.SendTextAsync(address, localizer["Voice.NotConfigured"], ct);
            return;
        }

        if (media.DurationSeconds is > MaxVoiceSeconds)
        {
            await channel.SendTextAsync(address, localizer["Voice.TooLong", MaxVoiceSeconds], ct);
            return;
        }

        // Charged before the transcription call, like receipts are charged before the vision
        // call — the daily allowance protects the thing that actually costs money, regardless
        // of whether the resulting transcript ends up resolving at L2 (free) or itself falls
        // through to its own separate L3 charge.
        if (!await usage.TryRecordL3CallAsync(spaceId, ct))
        {
            await channel.SendTextAsync(address, localizer["Usage.LimitExceeded"], ct);
            return;
        }

        using var content = await channel.DownloadMediaAsync(media.FileId, ct);
        var transcript = await transcription.TranscribeAsync(content, "voice.ogg", ct);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            await channel.SendTextAsync(address, localizer["Voice.NotUnderstood"], ct);
            return;
        }

        var replay = message with
        {
            Text = transcript,
            Media = [],
            ProviderMessageId = $"replay:{message.ProviderMessageId}",
        };
        await ProcessAsync(replay, ct);
    }

    // Internal static (rather than an instance method reading the captured `localizer` field)
    // so ExpenseHandlers can share it for /recurring's own frequency display — Reminders hasn't
    // been extracted yet (docs/13-piano-miglioramenti.md, F3), so this stays the one shared home
    // for both domains until it has.
    internal static string GetFrequencyDisplayName(RecurrenceFrequency frequency, IStringLocalizer<Messages> localizer) => frequency switch
    {
        RecurrenceFrequency.Daily => localizer["Reminders.FrequencyDaily"],
        RecurrenceFrequency.Weekly => localizer["Reminders.FrequencyWeekly"],
        RecurrenceFrequency.Monthly => localizer["Reminders.FrequencyMonthly"],
        _ => localizer["Reminders.FrequencyDaily"],
    };

    // Long form (day + month name), not numeric — "15/09" reads as 15 September for an
    // Italian user and September 15th for an American one; the day-first/month-first
    // ambiguity disappears once the month is spelled out (docs/09-localizzazione.md).
    internal static string FormatDueAt(DateTimeOffset dueAt, TimeZoneInfo timeZone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(dueAt, timeZone);
        return local.ToString("d MMMM, HH:mm", culture);
    }

    // Same numbers as the console's /spaces/{id}/usage page (SpaceUsage.razor), reusing the
    // exact same resx strings so the two never phrase this differently — just with a text bar
    // instead of a CSS one, since that's all Telegram can render.
    private async Task<string> HandleUsageCommandAsync(UsageService usage, Guid spaceId, CultureInfo culture, CancellationToken ct)
    {
        var (usedToday, limit, plan) = await usage.GetTodayUsageAsync(spaceId, ct);

        var priceLine = plan.MonthlyPrice == 0
            ? localizer["SpaceUsage.PriceFree"].Value
            : localizer["SpaceUsage.PriceLine", MoneyFormatter.Format(plan.MonthlyPrice, plan.Currency, culture.Name)].Value;

        var lines = new List<string>
        {
            $"{plan.Name} — {priceLine}",
            BuildUsageBar(usedToday, limit),
            localizer["SpaceUsage.CallsToday", usedToday, limit].Value,
        };

        if (usedToday >= limit)
        {
            lines.Add(localizer["SpaceUsage.LimitReachedNote"].Value);
        }

        return string.Join('\n', lines);
    }

    private static string BuildUsageBar(int usedToday, int limit)
    {
        const int segments = 10;
        var filled = limit <= 0 ? segments : Math.Clamp((int)Math.Round(usedToday * (double)segments / limit), 0, segments);
        return new string('█', filled) + new string('░', segments - filled);
    }

    // The fix for whoever got the wrong default culture (docs/09-localizzazione.md) — no
    // args shows the current one, "it"/"en" switches it, anything else is a usage hint.
    private async Task<string> HandleLanguageCommandAsync(
        AsyncServiceScope scope, User user, CultureInfo culture, string argsText, CancellationToken ct)
    {
        var requested = argsText.Trim().ToLowerInvariant();
        if (requested.Length == 0)
        {
            return localizer["Language.Current", culture.Name];
        }

        if (requested is not ("it" or "en"))
        {
            return localizer["Language.Usage"];
        }

        var provisioning = scope.ServiceProvider.GetRequiredService<UserProvisioningService>();
        await provisioning.SetPreferredCultureAsync(user.Id, requested, ct);

        var newCulture = new CultureInfo(requested);
        CultureInfo.CurrentCulture = newCulture;
        CultureInfo.CurrentUICulture = newCulture;

        return localizer["Language.Changed"];
    }

    // Same descriptions registered with Telegram's setMyCommands (Program.cs) — one source
    // of truth, so the menu and /help can't drift apart (docs/09-localizzazione.md). The final
    // line points at get_help (HandleGetHelpAsync) — the command list alone doesn't surface
    // that natural-language "how do I..." questions work too.
    private string HandleHelpCommand() => string.Join('\n', [
        $"/list — {localizer["Commands.List.Description"]}",
        $"/expense — {localizer["Commands.Expense.Description"]}",
        $"/remind — {localizer["Commands.Remind.Description"]}",
        $"/note — {localizer["Commands.Note.Description"]}",
        $"/recipes — {localizer["Commands.Recipes.Description"]}",
        $"/usage — {localizer["Commands.Usage.Description"]}",
        $"/month — {localizer["Commands.Month.Description"]}",
        $"/link — {localizer["Commands.Link.Description"]}",
        $"/language — {localizer["Commands.Language.Description"]}",
        $"/help — {localizer["Commands.Help.Description"]}",
        "",
        localizer["Help.MoreHint"].Value,
    ]);
}
