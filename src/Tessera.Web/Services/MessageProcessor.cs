using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
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
    IChannel channel,
    PartitionedRateLimiter<string> rateLimiter,
    IStringLocalizer<Messages> localizer,
    ILogger<MessageProcessor> logger,
    TelemetryClient? telemetry = null,
    LlmFallbackClient? llmFallback = null) : BackgroundService
{
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
        }
    }

    private async Task ProcessAsync(InboundMessage message, CancellationToken ct)
    {
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
            await HandleLlmReminderConfirmCallbackAsync(scope, address, user, remindConfirmCallback["remind.llmconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the reminder confirmation just above — the space is already fixed
        // in the pending payload.
        if (message.CallbackData is { } calendarConfirmCallback && calendarConfirmCallback.StartsWith("calendarEvent.llmconfirm:", StringComparison.Ordinal))
        {
            await HandleLlmCalendarEventConfirmCallbackAsync(scope, address, user, calendarConfirmCallback["calendarEvent.llmconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the two confirmations above.
        if (message.CallbackData is { } calendarDeleteCallback && calendarDeleteCallback.StartsWith("calendarEvent.deleteconfirm:", StringComparison.Ordinal))
        {
            await HandleLlmCalendarEventDeleteConfirmCallbackAsync(scope, address, user, calendarDeleteCallback["calendarEvent.deleteconfirm:".Length..], ct);
            return;
        }

        // Same reasoning as the three confirmations above.
        if (message.CallbackData is { } calendarMoveCallback && calendarMoveCallback.StartsWith("calendarEvent.moveconfirm:", StringComparison.Ordinal))
        {
            await HandleLlmCalendarEventMoveConfirmCallbackAsync(scope, address, user, calendarMoveCallback["calendarEvent.moveconfirm:".Length..], ct);
            return;
        }

        // CalendarToListSuggestionJob's own callback — "yes" carries the space id directly in
        // the callback data (it has no other pending state to fetch it from), "no" needs
        // nothing but the address to reply to.
        if (message.CallbackData is { } calendarSuggestCallback && calendarSuggestCallback.StartsWith("calendarSuggest.", StringComparison.Ordinal))
        {
            await HandleCalendarSuggestionCallbackAsync(scope, address, user, calendarSuggestCallback, ct);
            return;
        }

        // A tap on one of the "📎 note title" buttons under a notes list (HandleShowNotesAsync)
        // — the attachment carries its own SpaceId, so no space resolution is needed here.
        if (message.CallbackData is { } noteImageCallback && noteImageCallback.StartsWith("note.showimage:", StringComparison.Ordinal))
        {
            await HandleShowNoteAttachmentCallbackAsync(scope, address, user, noteImageCallback["note.showimage:".Length..], ct);
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

        if (message.Media.Count > 0 && nativeCommand == NativeCommand.Expense)
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
        // improve the router rather than guess at it.
        var routerLevel = message.Media.Count > 0 || message.CallbackData is not null || nativeCommand != NativeCommand.None
            ? "L1"
            : match is not null ? "L2" : "L3";
        telemetry?.TrackEvent($"Router{routerLevel}", new Dictionary<string, string> { ["Culture"] = culture.Name });

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

        if (message.Media.Count > 0 && nativeCommand == NativeCommand.Expense)
        {
            var receiptReply = await HandleReceiptAsync(
                scope, shopping, expenses, budgets, notifications, undo, onboarding, usage, address, spaceId, user, culture, message.Media[0], ct);
            if (receiptReply is not null)
            {
                await channel.SendTextAsync(address, receiptReply, ct);
            }

            return;
        }

        if (message.Media.Count > 0)
        {
            var mediaReply = await HandleIncomingMediaAsync(scope, notes, address, spaceId, user, text, message.Media[0], ct);
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
            await HandleCallbackAsync(shopping, expenses, reminders, budgets, notifications, undo, onboarding, address, spaceId, user, culture, callbackData, message.CallbackMessageId, ct);
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
                var remindReply = await HandleRemindCommandAsync(
                    reminders, undo, onboarding, address, spaceId, user, culture, text["/remind".Length..], ct);
                if (remindReply is not null)
                {
                    await channel.SendTextAsync(address, remindReply, ct);
                }
                return;
            }

            case NativeCommand.Recurring:
            {
                var recurringReply = await HandleRecurringCommandAsync(
                    recurringExpenses, spaceId, user, culture, text["/recurring".Length..], ct);
                if (recurringReply is not null)
                {
                    await channel.SendTextAsync(address, recurringReply, ct);
                }
                return;
            }

            case NativeCommand.Budget:
            {
                var budgetReply = await HandleBudgetCommandAsync(
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
                var digestReply = await HandleDigestCommandAsync(digest, expenses, spaceId, user, culture, ct);
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
                var listReply = await HandleShowAsync(shopping, address, spaceId, user.Id, listName: null, ct);
                if (listReply is not null)
                {
                    await channel.SendTextAsync(address, listReply, ct);
                }
                return;
            }

            case NativeCommand.Expense:
            {
                var expenseReply = await HandleExpenseCommandAsync(
                    expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture, text["/expense".Length..], ct);
                if (expenseReply is not null)
                {
                    await channel.SendTextAsync(address, expenseReply, ct);
                }
                return;
            }

            case NativeCommand.Month:
            {
                var monthReply = await HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct);
                await channel.SendTextAsync(address, monthReply, ct);
                return;
            }

            case NativeCommand.Note:
            {
                var noteReply = await HandleNoteCommandAsync(
                    scope, notes, undo, onboarding, address, spaceId, user.Id, text["/note".Length..], ct);
                if (noteReply is not null)
                {
                    await channel.SendTextAsync(address, noteReply, ct);
                }
                return;
            }
        }

        string? reply;
        if (match is not null && match.Intent != "reminders.natural" && match.Intent != "calendar.natural")
        {
            reply = match.Intent switch
            {
                "shopping.add" => await HandleAddAsync(
                    shopping, notifications, undo, onboarding, address, spaceId, user.Id, match.Slots["item"], listName: null, ct),
                "shopping.show" => await HandleShowAsync(shopping, address, spaceId, user.Id, listName: null, ct),
                "shopping.check" => await HandleCheckAsync(
                    shopping, notifications, undo, address, spaceId, user.Id, match.Slots["item"], listName: null, ct),
                "shopping.remove" => await HandleRemoveAsync(shopping, spaceId, user.Id, match.Slots["item"], listName: null, ct),
                "shopping.clear" => await HandleClearAsync(shopping, undo, address, spaceId, user.Id, listName: null, ct),
                "expenses.add" => await HandleExpenseAddAsync(
                    expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
                    match.Slots["amount"], match.Slots.GetValueOrDefault("category"), match.Slots.GetValueOrDefault("merchant"), ct),
                "expenses.query" => await HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct),
                "expenses.query.category" => await HandleExpensesQueryByCategoryAsync(
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
                scope, shopping, expenses, reminders, notes, budgets, notifications, undo, onboarding, address, spaceId, user, culture, text, ct);
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
        AsyncServiceScope scope, ShoppingListService shopping, ExpenseService expenses, ReminderService reminders,
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
        var context = new LlmContext(culture.Name, user.TimeZoneId ?? "UTC", DateTimeOffset.UtcNow, space.Name, recentAction?.Description);

        var result = await llmFallback.TryCompleteAsync(text, context, ct);
        if (result is null)
        {
            // The deterministic paths must survive an Azure OpenAI outage
            // (docs/06-roadmap.md) — this is the same honest reply as "not configured".
            return await SendNotUnderstoodAsync(address, text, culture, ct);
        }

        if (result.ToolCall is null)
        {
            return result.ReplyText ?? await SendNotUnderstoodAsync(address, text, culture, ct);
        }

        var args = result.ToolCall.Arguments;
        return result.ToolCall.Name switch
        {
            LlmTools.AddShoppingItem => await HandleAddAsync(
                shopping, notifications, undo, onboarding, address, spaceId, user.Id,
                args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), ct),
            LlmTools.CheckShoppingItem => await HandleCheckAsync(
                shopping, notifications, undo, address, spaceId, user.Id,
                args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), ct),
            LlmTools.RemoveShoppingItem => await HandleRemoveAsync(
                shopping, spaceId, user.Id, args.GetProperty("item").GetString() ?? "", GetOptionalString(args, "list"), ct),
            LlmTools.ShowShoppingList => await HandleShowAsync(shopping, address, spaceId, user.Id, GetOptionalString(args, "list"), ct),
            LlmTools.ClearShoppingList => await HandleClearAsync(shopping, undo, address, spaceId, user.Id, GetOptionalString(args, "list"), ct),
            LlmTools.ListShoppingLists => await HandleListShoppingListsAsync(shopping, spaceId, user.Id, ct),
            LlmTools.RecordExpense => await RecordExpenseAndReplyAsync(
                expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
                args.GetProperty("amount").GetDecimal(),
                args.TryGetProperty("category", out var categoryProp) ? categoryProp.GetString() : null,
                args.TryGetProperty("merchant", out var merchantProp) ? merchantProp.GetString() : null, ct),
            LlmTools.QueryMonthlyExpenses => await HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct),
            LlmTools.QueryExpenseHistory => await HandleHistoryQueryAsync(expenses, spaceId, user, culture, args, ct),
            LlmTools.CreateReminder => await HandleLlmReminderAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.CreateNote => await CreateNoteAndReplyAsync(
                notes, undo, onboarding, address, spaceId, user.Id,
                GetOptionalString(args, "title"), args.GetProperty("body").GetString() ?? "", ct),
            LlmTools.ShowNotes => await HandleShowNotesAsync(scope, address, notes, spaceId, user.Id, ct),
            LlmTools.DeleteNote => await HandleLlmDeleteNoteAsync(
                scope, notes, spaceId, user.Id, args.GetProperty("search_text").GetString() ?? "", ct),
            LlmTools.QueryCalendarEvents => await HandleCalendarEventsQueryAsync(scope, spaceId, user, culture, args, ct),
            LlmTools.QueryCalendarFreeBusy => await HandleCalendarFreeBusyQueryAsync(scope, spaceId, user, culture, args, ct),
            LlmTools.CreateCalendarEvent => await HandleLlmCreateCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.DeleteCalendarEvent => await HandleLlmDeleteCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.MoveCalendarEvent => await HandleLlmMoveCalendarEventAsync(scope, address, spaceId, user, culture, args, ct),
            LlmTools.CorrectLastShoppingItem when recentAction is not null => await HandleShoppingCorrectionAsync(
                shopping, address, spaceId, user.Id, recentAction.ItemId, args.GetProperty("corrected_text").GetString() ?? "", ct),
            _ => await SendNotUnderstoodAsync(address, text, culture, ct),
        };
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

    private async Task<string?> HandleShoppingCorrectionAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid itemId,
        string correctedText, CancellationToken ct)
    {
        var item = await shopping.CorrectItemAsync(spaceId, userId, itemId, correctedText, ct);
        if (item is null)
        {
            return localizer["Correction.Conflict"];
        }

        await SendWithUndoAsync(address, localizer["Shopping.ItemAdded", item.RawText], ct);
        return null;
    }

    // Generic lists beyond groceries (docs/10-conversazione.md) — ShoppingList.Name already
    // supported this; "which list?" only needs answering when the model asks about it.
    private async Task<string> HandleListShoppingListsAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var lists = await shopping.GetListsAsync(spaceId, userId, ct);
        return lists.Count == 0
            ? localizer["Shopping.NoLists"]
            : string.Join(", ", lists.Select(l => string.IsNullOrEmpty(l.Name) ? localizer["Shopping.DefaultListName"].Value : l.Name));
    }

    private static string? GetOptionalString(JsonElement args, string propertyName) =>
        args.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    private sealed record PendingLlmReminder(Guid SpaceId, string Text, DateTimeOffset DueAt, string TimeZoneId);

    // The model's interpreted date is never committed straight away — it's read back to the
    // user first (docs/05-ottimizzazioni.md: "l'unico modo per intercettare l'interpretazione
    // sbagliata prima che diventi un promemoria inutile"), the same ConversationState-backed
    // ask-and-replay mechanism the space disambiguation question uses.
    private async Task<string?> HandleLlmReminderAsync(
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

        var prompt = localizer["Reminders.ConfirmPrompt", reminderText, FormatDueAt(dueAt, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "remind.llmconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "remind.llmconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    private async Task HandleLlmReminderConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "reminder.llmConfirm" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
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
        var confirmReply = localizer["Reminders.CreatedOnce", FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
        await FinalizeUsefulActionReplyAsync(onboarding, address, user.Id, "reminders", confirmReply, ct);
    }

    // Read-only, so no confirmation round trip is needed — only creating something from an
    // interpreted date goes through that (hard rule 14).
    private async Task<string> HandleCalendarEventsQueryAsync(
        AsyncServiceScope scope, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (!TryParseLocalDateTime(GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(GetOptionalString(args, "to"), timeZone, out var to))
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
            localizer["Calendars.EventLine", FormatDueAt(e.Start, timeZone, culture), e.Title].Value));
    }

    private async Task<string> HandleCalendarFreeBusyQueryAsync(
        AsyncServiceScope scope, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var calendarQuery = scope.ServiceProvider.GetService<CalendarQueryService>();
        if (calendarQuery is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (!TryParseLocalDateTime(GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(GetOptionalString(args, "to"), timeZone, out var to))
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

        return string.Join('\n', busy.Select(b =>
            localizer["Calendars.BusyLine", FormatDueAt(b.Start, timeZone, culture), FormatDueAt(b.End, timeZone, culture)].Value));
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

    // Same read-back-before-committing pattern as HandleLlmReminderAsync (hard rule 14) — an
    // event actually created on the user's real Google Calendar is much more visible (and
    // awkward to silently undo) than a reminder, so misreading the date matters even more here.
    private async Task<string?> HandleLlmCreateCalendarEventAsync(
        AsyncServiceScope scope, ChannelAddress address, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        if (scope.ServiceProvider.GetService<CalendarQueryService>() is null)
        {
            return localizer["Calendars.NotConfigured"];
        }

        var title = args.GetProperty("title").GetString();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId ?? "UTC");
        if (string.IsNullOrWhiteSpace(title)
            || !TryParseLocalDateTime(GetOptionalString(args, "start"), timeZone, out var start)
            || !TryParseLocalDateTime(GetOptionalString(args, "end"), timeZone, out var end))
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

        var prompt = localizer["Calendars.ConfirmEventPrompt", title, FormatDueAt(start, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.llmconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.llmconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    private async Task HandleLlmCalendarEventConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.llmConfirm" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
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
        var confirmReply = localizer["Calendars.EventCreated", created.Title, FormatDueAt(created.Start, timeZone, culture)].Value;
        await channel.SendTextAsync(address, confirmReply, ct);
    }

    private sealed record PendingLlmCalendarEventDelete(Guid SpaceId, Guid ExternalCalendarId, string ProviderEventId, string Title, DateTimeOffset Start);

    // Search-then-confirm, same read-back-before-committing reasoning as creation (hard rule
    // 14) — deleting the wrong event on someone's real calendar is far worse than a mis-typed
    // reminder, so this never deletes straight off a single LLM call.
    private async Task<string?> HandleLlmDeleteCalendarEventAsync(
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
            || !TryParseLocalDateTime(GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(GetOptionalString(args, "to"), timeZone, out var to))
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
            var lines = matches.Select(e => localizer["Calendars.EventLine", FormatDueAt(e.Start, timeZone, culture), e.Title].Value);
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

        var prompt = localizer["Calendars.ConfirmDeleteEventPrompt", match.Title, FormatDueAt(match.Start, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.deleteconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.deleteconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    private async Task HandleLlmCalendarEventDeleteConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.deleteConfirm" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
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
    private async Task<string?> HandleLlmMoveCalendarEventAsync(
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
            || !TryParseLocalDateTime(GetOptionalString(args, "from"), timeZone, out var from)
            || !TryParseLocalDateTime(GetOptionalString(args, "to"), timeZone, out var to)
            || !TryParseLocalDateTime(GetOptionalString(args, "new_start"), timeZone, out var newStart))
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
            var lines = matches.Select(e => localizer["Calendars.EventLine", FormatDueAt(e.Start, timeZone, culture), e.Title].Value);
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

        var prompt = localizer["Calendars.ConfirmMoveEventPrompt", match.Title, FormatDueAt(match.Start, timeZone, culture), FormatDueAt(newStart, timeZone, culture)];
        var choices = new[]
        {
            new Choice(localizer["Reminders.ConfirmYes"].Value, "calendarEvent.moveconfirm:yes"),
            new Choice(localizer["Reminders.ConfirmNo"].Value, "calendarEvent.moveconfirm:no"),
        };
        await channel.SendChoicesAsync(address, prompt, choices, ct);
        return null;
    }

    private async Task HandleLlmCalendarEventMoveConfirmCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string choice, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<TesseraDbContext>();
        var state = await db.ConversationStates.FirstOrDefaultAsync(
            s => s.UserId == user.Id && s.PendingIntent == "calendarEvent.moveConfirm" && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
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
            ? localizer["Calendars.EventMoved", payload.Title, FormatDueAt(moved.Start, timeZone, culture)].Value
            : localizer["Calendars.MoveEventFailed"].Value;
        await channel.SendTextAsync(address, reply, ct);
    }

    // "Yes" reuses the exact same shopping-list reply the router's shopping.show/ShowShoppingList
    // path sends — no separate rendering logic, no pending state to store, since which space to
    // show came along in the callback data itself.
    private async Task HandleCalendarSuggestionCallbackAsync(
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
        var reply = await HandleShowAsync(shopping, address, spaceId, user.Id, listName: null, ct);
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
        "remind.complete" => (ResourceKind.Reminders, AccessLevel.Write),
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
        ShoppingListService shopping, ExpenseService expenses, ReminderService reminders, BudgetService budgets,
        NotificationService notifications, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, string callbackData, string? callbackMessageId, CancellationToken ct)
    {
        var parts = callbackData.Split(':');

        if (parts.Length == 2 && parts[0] == "shopping.check" && Guid.TryParse(parts[1], out var itemId))
        {
            await HandleShoppingCheckCallbackAsync(shopping, notifications, undo, address, spaceId, user.Id, itemId, callbackMessageId, ct);
            return;
        }

        if (parts.Length == 2 && parts[0] == "shopping.remove" && Guid.TryParse(parts[1], out var removeItemId))
        {
            await HandleShoppingRemoveCallbackAsync(shopping, address, spaceId, user.Id, removeItemId, callbackMessageId, ct);
            return;
        }

        if (parts.Length == 3 && parts[0] == "expcat"
            && Guid.TryParse(parts[1], out var expenseId) && int.TryParse(parts[2], out var categoryIndex))
        {
            await HandleExpenseCategorizeCallbackAsync(expenses, address, spaceId, expenseId, categoryIndex, ct);
            return;
        }

        if (parts.Length == 3 && parts[0] == "expconfirm" && Guid.TryParse(parts[1], out var pendingId))
        {
            await HandleExpenseConfirmCallbackAsync(
                expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture, pendingId, parts[2], ct);
            return;
        }

        if (parts.Length == 2 && parts[0] == "remind.complete" && Guid.TryParse(parts[1], out var reminderId))
        {
            await HandleReminderCompleteCallbackAsync(reminders, address, spaceId, user.Id, reminderId, ct);
        }
    }

    private async Task HandleReminderCompleteCallbackAsync(
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

    private async Task HandleShoppingCheckCallbackAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo, ChannelAddress address,
        Guid spaceId, Guid userId, Guid itemId, string? callbackMessageId, CancellationToken ct)
    {
        var item = await shopping.CheckItemByIdAsync(spaceId, userId, itemId, ct);
        if (item is null)
        {
            // Already checked by a concurrent tap/command, or the list was cleared since —
            // the button is stale. Nothing to report; the reply for the original tap
            // already dismissed the loading state (TelegramUpdateIngestor).
            return;
        }

        await notifications.NotifyAsync(
            new ShoppingItemChecked(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        await undo.RecordShoppingCheckAsync(userId, spaceId, item.Id, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
        await RefreshShoppingListMessageAsync(shopping, address, spaceId, userId, item.ShoppingListId, callbackMessageId, ct);
    }

    // No undo here: removing via the list's 🗑 button matches HandleRemoveAsync's own text-
    // command behavior, which has never offered one either — adding it would need a new
    // ShoppingRemoveUndoPayload, out of scope for what was asked (a remove button).
    private async Task HandleShoppingRemoveCallbackAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid itemId,
        string? callbackMessageId, CancellationToken ct)
    {
        var item = await shopping.RemoveItemByIdAsync(spaceId, userId, itemId, ct);
        if (item is null)
        {
            // Already removed by a concurrent tap/command — the button is stale.
            return;
        }

        await channel.SendTextAsync(address, localizer["Shopping.ItemRemoved", item.RawText], ct);
        await RefreshShoppingListMessageAsync(shopping, address, spaceId, userId, item.ShoppingListId, callbackMessageId, ct);
    }

    private async Task HandleExpenseCategorizeCallbackAsync(
        ExpenseService expenses, ChannelAddress address, Guid spaceId, Guid expenseId, int categoryIndex, CancellationToken ct)
    {
        var categories = await expenses.GetCategoriesAsync(spaceId, ct);
        if (categoryIndex < 0 || categoryIndex >= categories.Count)
        {
            return;
        }
        var category = categories[categoryIndex];

        var expense = await expenses.SetCategoryAsync(spaceId, expenseId, category.Id, ct);
        if (expense is null)
        {
            // Stale button: the expense no longer exists, or already got a category
            // from another tap. Nothing to report.
            return;
        }

        if (expense.Merchant is not null)
        {
            await expenses.LearnMerchantCategoryAsync(spaceId, expense.Merchant, category.Id, ct);
        }

        await channel.SendTextAsync(
            address, localizer["Expenses.CategorySaved", expense.Merchant ?? "", GetCategoryDisplayName(category, localizer)], ct);
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
        space.GroupChatId = message.ExternalChatId;
        await db.SaveChangesAsync(ct);

        await channel.SendTextAsync(address, localizer["Group.Welcome", adder.DisplayName ?? adder.Email], ct);
    }

    private async Task<string?> HandleAddAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo, OnboardingService onboarding,
        ChannelAddress address, Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        var item = await shopping.AddItemAsync(spaceId, userId, itemText, listName, ct);
        await notifications.NotifyAsync(
            new ShoppingItemAdded(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, ct);
        await FinalizeUsefulActionReplyAsync(onboarding, address, userId, "shopping", localizer["Shopping.ItemAdded", item.RawText].Value, ct);
        return null;
    }

    private async Task<string?> HandleShowAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, string? listName, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, listName, ct);
        if (items.Count == 0)
        {
            return localizer["Shopping.ListEmpty"];
        }

        var (text, rows) = BuildShoppingListView(items);
        if (rows.Count == 0)
        {
            await channel.SendTextAsync(address, text, ct);
        }
        else
        {
            await channel.SendGroupedChoicesAsync(address, text, rows, ct);
        }

        return null;
    }

    // Shared by HandleShowAsync (first render) and the check/remove callbacks (in-place
    // refresh) so the two never drift into different renderings of the same list. A ✓/🗑 pair
    // per unchecked item, in the same row — the closest Telegram gets to "buttons beside each
    // list line," since inline keyboards always render as a block below the text, never
    // interleaved with it.
    private (string Text, List<IReadOnlyList<Choice>> Rows) BuildShoppingListView(IReadOnlyList<ShoppingItem> items)
    {
        if (items.Count == 0)
        {
            return (localizer["Shopping.ListEmpty"].Value, []);
        }

        var lines = items.Select(i => localizer[
            i.IsChecked ? "Shopping.ListItemLineChecked" : "Shopping.ListItemLine", i.RawText].Value);
        var text = string.Join('\n', lines);

        var rows = items
            .Where(i => !i.IsChecked)
            .Select(i => (IReadOnlyList<Choice>)new[]
            {
                new Choice(localizer["Shopping.CheckButtonLabel", i.RawText].Value, $"shopping.check:{i.Id}"),
                new Choice(localizer["Shopping.RemoveButtonLabel"].Value, $"shopping.remove:{i.Id}"),
            })
            .ToList();

        return (text, rows);
    }

    // Best-effort: no-ops if this check/remove wasn't triggered by a button tap (a text command
    // or an LLM tool call has no original list message to refresh) or if the edit itself fails.
    private async Task RefreshShoppingListMessageAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, Guid listId,
        string? callbackMessageId, CancellationToken ct)
    {
        if (callbackMessageId is null)
        {
            return;
        }

        var items = await shopping.GetItemsByListIdAsync(spaceId, userId, listId, ct);
        var (text, rows) = BuildShoppingListView(items);
        await channel.EditListMessageAsync(address, callbackMessageId, text, rows, ct);
    }

    // Checking off an item doesn't count toward onboarding progression (docs/10-conversazione.md
    // frames it around content-creating actions — add, expense, reminder — not state changes on
    // things already there; counting every check would fire the sharing prompt after one trip
    // through the shopping list). It still gets the undo button.
    private async Task<string?> HandleCheckAsync(
        ShoppingListService shopping, NotificationService notifications, UndoService undo,
        ChannelAddress address, Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        var item = await shopping.CheckItemAsync(spaceId, userId, itemText, listName, ct);
        if (item is null)
        {
            return localizer["Shopping.ItemNotFound", itemText];
        }

        await notifications.NotifyAsync(
            new ShoppingItemChecked(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        await undo.RecordShoppingCheckAsync(userId, spaceId, item.Id, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
        return null;
    }

    private async Task<string> HandleRemoveAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        var item = await shopping.RemoveItemAsync(spaceId, userId, itemText, listName, ct);
        return item is null
            ? localizer["Shopping.ItemNotFound", itemText]
            : localizer["Shopping.ItemRemoved", item.RawText];
    }

    private async Task<string?> HandleClearAsync(
        ShoppingListService shopping, UndoService undo, ChannelAddress address, Guid spaceId, Guid userId,
        string? listName, CancellationToken ct)
    {
        var cleared = await shopping.ClearAsync(spaceId, userId, listName, ct);
        await undo.RecordShoppingClearAsync(userId, spaceId, cleared, ct);
        await SendWithUndoAsync(address, localizer["Shopping.ListCleared"], ct);
        return null;
    }

    private async Task<string?> HandleExpenseAddAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, UndoService undo,
        OnboardingService onboarding, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        string amountText, string? categoryText, string? merchantText, CancellationToken ct)
    {
        if (!decimal.TryParse(amountText, NumberStyles.Number, culture, out var amount))
        {
            return localizer["Expenses.InvalidAmount", amountText];
        }

        // "1,5" in en, "1.500" in it: decimal.TryParse accepts these without error, but
        // picks a value that could be off by a factor of 10-100 — worth a tap to confirm
        // rather than a silent guess (docs/09-localizzazione.md).
        if (AmountAmbiguity.IsAmbiguous(amountText, culture))
        {
            var (asGrouped, asDecimal) = AmountAmbiguity.GetCandidates(amountText, culture);
            var pending = await expenses.CreatePendingConfirmationAsync(
                spaceId, user.Id, asGrouped, asDecimal, categoryText, merchantText, ct);
            var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);

            var choices = new[]
            {
                new Choice(MoneyFormatter.Format(asGrouped, currency, culture.Name), $"expconfirm:{pending.Id}:g"),
                new Choice(MoneyFormatter.Format(asDecimal, currency, culture.Name), $"expconfirm:{pending.Id}:d"),
            };
            await channel.SendChoicesAsync(address, localizer["Expenses.ConfirmAmount"], choices, ct);
            return null;
        }

        return await RecordExpenseAndReplyAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture, amount, categoryText, merchantText, ct);
    }

    // The trivial form of the /expense menu command — amount plus an optional free-text
    // category, no merchant slot (that's the natural-language path's job).
    private async Task<string?> HandleExpenseCommandAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, UndoService undo,
        OnboardingService onboarding, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        string argsText, CancellationToken ct)
    {
        var command = ExpenseCommandParser.Parse(argsText);
        if (command is null)
        {
            return localizer["Expenses.Usage"];
        }

        return await HandleExpenseAddAsync(expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
            command.AmountText, command.CategoryText, merchantText: null, ct);
    }

    private async Task HandleExpenseConfirmCallbackAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, UndoService undo,
        OnboardingService onboarding, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        Guid pendingId, string choice, CancellationToken ct)
    {
        var pending = await expenses.ConsumePendingConfirmationAsync(spaceId, pendingId, ct);
        if (pending is null)
        {
            // Expired, or already resolved by a previous tap — the button is stale.
            return;
        }

        var amount = choice == "g" ? pending.CandidateAsGrouped : pending.CandidateAsDecimal;
        await RecordExpenseAndReplyAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user, culture,
            amount, pending.CategoryText, pending.MerchantText, ct);
    }

    // Shared by the direct (unambiguous) path and the post-confirmation path, so recording
    // and the categorization precedence (docs/02-modello-dati.md) can't drift between them.
    // Single exit point: every branch converges on (expense, reply) before recording the undo
    // and sending, so the undo button and onboarding hint attach no matter which path was taken.
    private async Task<string?> RecordExpenseAndReplyAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, UndoService undo,
        OnboardingService onboarding, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        decimal amount, string? categoryText, string? merchantText, CancellationToken ct)
    {
        var today = GetUserToday(user);
        Expense expense;
        string reply;

        // An explicit category always wins — there is nothing to resolve or learn.
        if (categoryText is not null)
        {
            var category = await ResolveCategoryAsync(expenses, spaceId, categoryText, ct);
            expense = await expenses.RecordAsync(spaceId, user.Id, amount, category?.Id, merchant: null, today, note: null, ct);
            await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);
            var formatted = MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name);
            reply = category is null
                ? localizer["Expenses.Recorded", formatted]
                : localizer["Expenses.RecordedWithCategory", formatted, GetCategoryDisplayName(category, localizer)];
        }
        else if (merchantText is null)
        {
            // No merchant either: nothing to categorize, nothing to learn from.
            expense = await expenses.RecordAsync(spaceId, user.Id, amount, categoryId: null, merchant: null, today, note: null, ct);
            await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);
            reply = localizer["Expenses.Recorded", MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name)];
        }
        else
        {
            // Categorization strategy, in order of precedence (docs/02-modello-dati.md):
            // 1. learned merchant → category mapping, applied silently;
            // 4. unknown merchant → ask once via inline keyboard, and the answer feeds back
            //    into the mapping so this merchant is never asked about again.
            var learnedCategory = await expenses.FindMerchantCategoryAsync(spaceId, merchantText, ct);
            expense = await expenses.RecordAsync(spaceId, user.Id, amount, learnedCategory?.Id, merchantText, today, note: null, ct);
            await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);
            var recordedFormatted = MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name);

            if (learnedCategory is not null)
            {
                reply = localizer["Expenses.RecordedWithMerchantAndCategory",
                    recordedFormatted, merchantText, GetCategoryDisplayName(learnedCategory, localizer)];
            }
            else
            {
                await SendCategoryPickerAsync(expenses, address, spaceId, expense.Id, merchantText, ct);
                // No category yet — nothing to check against a per-category budget, only the overall one.
                reply = localizer["Expenses.RecordedWithMerchant", recordedFormatted, merchantText];
            }
        }

        await undo.RecordExpenseAsync(user.Id, spaceId, expense.Id, ct);
        var replyWithAlerts = await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, expense, reply, ct);
        await FinalizeUsefulActionReplyAsync(onboarding, address, user.Id, "expenses", replyWithAlerts, ct);
        return null;
    }

    private static async Task NotifyExpenseRecordedAsync(
        NotificationService notifications, Guid spaceId, Guid actorUserId, Expense expense, ChannelAddress address, CancellationToken ct) =>
        await notifications.NotifyAsync(
            new ExpenseRecorded(spaceId, actorUserId, expense.Amount, expense.Currency, expense.CategoryId, address.ExternalChatId, DateTimeOffset.UtcNow),
            ct);

    private async Task<string> AppendBudgetAlertsAsync(
        ExpenseService expenses, BudgetService budgets, Guid spaceId, Guid userId, CultureInfo culture,
        Expense expense, string reply, CancellationToken ct)
    {
        var alerts = await budgets.CheckThresholdsAsync(spaceId, userId, expense.CategoryId, expense.Date, ct);
        if (alerts.Count == 0)
        {
            return reply;
        }

        var categories = await expenses.GetCategoriesAsync(spaceId, ct);
        var lines = new List<string> { reply };
        foreach (var alert in alerts)
        {
            var spentFormatted = MoneyFormatter.Format(alert.Spent, expense.Currency, culture.Name);
            var limitFormatted = MoneyFormatter.Format(alert.Limit, expense.Currency, culture.Name);
            if (alert.CategoryId is null)
            {
                lines.Add(localizer["Budget.AlertOverall", spentFormatted, limitFormatted]);
                continue;
            }

            var category = categories.FirstOrDefault(c => c.Id == alert.CategoryId);
            var categoryName = category is null ? "" : GetCategoryDisplayName(category, localizer);
            lines.Add(localizer["Budget.AlertCategory", spentFormatted, limitFormatted, categoryName]);
        }

        return string.Join('\n', lines);
    }

    private async Task SendCategoryPickerAsync(
        ExpenseService expenses, ChannelAddress address, Guid spaceId, Guid expenseId, string merchant, CancellationToken ct)
    {
        var categories = await expenses.GetCategoriesAsync(spaceId, ct);
        var choices = categories
            .Select((category, index) => new Choice(GetCategoryDisplayName(category, localizer), $"expcat:{expenseId}:{index}"))
            .ToList();

        await channel.SendChoicesAsync(address, localizer["Expenses.AskCategoryForMerchant", merchant], choices, ct);
    }

    private async Task<string> HandleExpensesQueryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, CancellationToken ct)
    {
        // Specific months ("a gennaio") aren't supported yet — query_monthly_expenses (the L3
        // tool, docs/05-ottimizzazioni.md) has no month parameter either. This always answers
        // for the current month and says so explicitly, rather than guessing wrong.
        var today = GetUserToday(user);
        var (total, currency) = await expenses.GetMonthlyTotalAsync(spaceId, user.Id, today.Year, today.Month, ct);

        var monthName = culture.DateTimeFormat.GetMonthName(today.Month);
        var formatted = MoneyFormatter.Format(total, currency, culture.Name);
        return localizer["Expenses.MonthlyTotal", monthName, formatted];
    }

    private async Task<string> HandleExpensesQueryByCategoryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, string categoryText, CancellationToken ct)
    {
        var category = await ResolveCategoryAsync(expenses, spaceId, categoryText, ct);
        if (category is null)
        {
            return localizer["Expenses.CategoryNotFound", categoryText];
        }

        var today = GetUserToday(user);
        var (total, currency) = await expenses.GetCategoryTotalAsync(spaceId, user.Id, category.Id, today.Year, today.Month, ct);

        var formatted = MoneyFormatter.Format(total, currency, culture.Name);
        return localizer["Expenses.CategoryTotal", formatted, GetCategoryDisplayName(category, localizer)];
    }

    // Historical search (docs/10-conversazione.md) — always an L3 tool, never pattern
    // matching: "the variety of phrasings is too high". The reply is a computed aggregate,
    // read through resx like everything else — the model supplies parameters, not prose.
    private async Task<string> HandleHistoryQueryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var aggregationText = args.GetProperty("aggregation").GetString();
        if (!Enum.TryParse<HistoryAggregation>(aggregationText, ignoreCase: true, out var aggregation))
        {
            aggregation = HistoryAggregation.Total;
        }

        Guid? categoryId = null;
        if (GetOptionalString(args, "category") is { } categoryText)
        {
            categoryId = (await ResolveCategoryAsync(expenses, spaceId, categoryText, ct))?.Id;
        }

        var dateFrom = ParseOptionalDate(GetOptionalString(args, "date_from"));
        var dateTo = ParseOptionalDate(GetOptionalString(args, "date_to"));
        var searchText = GetOptionalString(args, "search_text");

        var result = await expenses.QueryHistoryAsync(spaceId, user.Id, searchText, categoryId, dateFrom, dateTo, aggregation, ct);

        return aggregation switch
        {
            HistoryAggregation.MostRecentDate => result.MostRecentDate is { } date
                ? localizer["History.MostRecentDate", date.ToString("d MMMM yyyy", culture)]
                : localizer["History.NotFound"],
            HistoryAggregation.Count => localizer["History.Count", result.Count],
            HistoryAggregation.Average => result.Amount is { } average
                ? localizer["History.Average", MoneyFormatter.Format(average, result.Currency!, culture.Name)]
                : localizer["History.NotFound"],
            _ => localizer["History.Total", MoneyFormatter.Format(result.Amount ?? 0m, result.Currency ?? "EUR", culture.Name)],
        };
    }

    private static DateOnly? ParseOptionalDate(string? text) =>
        text is not null && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private async Task<Category?> ResolveCategoryAsync(ExpenseService expenses, Guid spaceId, string text, CancellationToken ct)
    {
        var categories = await expenses.GetCategoriesAsync(spaceId, ct);
        var target = text.Trim().ToLowerInvariant();

        foreach (var category in categories)
        {
            var displayName = GetCategoryDisplayName(category, localizer).ToLowerInvariant();
            if (displayName.Contains(target) || target.Contains(displayName))
            {
                return category;
            }
        }

        return null;
    }

    // System categories are resource keys and localize; user categories are free text
    // and never translate (docs/09-localizzazione.md) — never mixed on the same row.
    // Internal static so DigestFormatter can reuse it without threading an instance through.
    internal static string GetCategoryDisplayName(Category category, IStringLocalizer<Messages> localizer) =>
        category.ResourceKey is not null ? localizer[category.ResourceKey] : category.Name ?? "";

    private static DateOnly GetUserToday(User user)
    {
        var timeZone = user.TimeZoneId is null
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }

    private async Task<string?> HandleRemindCommandAsync(
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
                var onceReply = localizer["Reminders.CreatedOnce", FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
                await FinalizeUsefulActionReplyAsync(onboarding, address, user.Id, "reminders", onceReply, ct);
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
                    GetFrequencyDisplayName(recurring.Frequency), FormatDueAt(reminder.DueAt, timeZone, culture)].Value;
                await FinalizeUsefulActionReplyAsync(onboarding, address, user.Id, "reminders", recurringReply, ct);
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
            localizer["Reminders.ListItemLine", FormatDueAt(r.DueAt, timeZone, culture), r.Text].Value);
        var text = string.Join('\n', lines);

        // One "done" button per reminder — a tap is a callback_query, the same L1 pattern
        // as checking off a shopping list item (docs/05-ottimizzazioni.md).
        var choices = pending.Select(r => new Choice(r.Text, $"remind.complete:{r.Id}")).ToList();
        await channel.SendChoicesAsync(address, text, choices, ct);
        return null;
    }

    // /note is a native L1 command: bare lists, anything else is free text saved verbatim as
    // the body (no title) — the model-driven create_note tool is what fills in a title, when
    // the phrasing has one to extract.
    private async Task<string?> HandleNoteCommandAsync(
        AsyncServiceScope scope, NoteService notes, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, Guid userId, string argsText, CancellationToken ct)
    {
        var trimmed = argsText.Trim();
        return trimmed.Length == 0
            ? await HandleShowNotesAsync(scope, address, notes, spaceId, userId, ct)
            : await CreateNoteAndReplyAsync(notes, undo, onboarding, address, spaceId, userId, title: null, trimmed, ct);
    }

    // Sends the list itself (rather than just returning text) because notes with an image
    // attachment get a button to reveal it on demand — showing every image unprompted on every
    // "what notes are there?" would get noisy fast once a space has more than a couple.
    // One message per note, not one giant list — a note's "show attachment" button then sits
    // directly under that note's own text instead of in a single wall of buttons at the end,
    // disconnected from which note each one belonged to.
    private async Task<string?> HandleShowNotesAsync(
        AsyncServiceScope scope, ChannelAddress address, NoteService notes, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var all = await notes.GetNotesAsync(spaceId, userId, ct);
        if (all.Count == 0)
        {
            return localizer["Notes.ListEmpty"];
        }

        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        foreach (var note in all)
        {
            var text = note.Title is { Length: > 0 }
                ? localizer["Notes.ListItemLineTitled", note.Title, note.Body].Value
                : localizer["Notes.ListItemLine", note.Body].Value;

            List<Choice> imageChoices = [];
            if (attachments is not null)
            {
                var noteAttachments = await attachments.GetForAsync(ResourceKind.Notes, note.Id, ct);
                imageChoices = noteAttachments
                    .Where(a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    .Select(a => new Choice(localizer["Notes.ShowAttachmentButton"].Value, $"note.showimage:{a.Id}"))
                    .ToList();
            }

            if (imageChoices.Count == 0)
            {
                await channel.SendTextAsync(address, text, ct);
            }
            else
            {
                await channel.SendChoicesAsync(address, text, imageChoices, ct);
            }
        }

        return null;
    }

    // note.showimage: callback — resolved lazily on tap rather than eagerly with the list
    // (HandleShowNotesAsync above), so a space with many photo-attached notes doesn't flood
    // the chat. The attachment's own SpaceId is the permission check here, not the caller's
    // current space, since by the time this fires the user may be in any conversation context.
    private async Task HandleShowNoteAttachmentCallbackAsync(
        AsyncServiceScope scope, ChannelAddress address, User user, string attachmentIdText, CancellationToken ct)
    {
        var attachmentService = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachmentService is null || !Guid.TryParse(attachmentIdText, out var attachmentId))
        {
            return;
        }

        var attachment = await attachmentService.GetByIdAsync(attachmentId, ct);
        if (attachment is null || attachment.Resource != ResourceKind.Notes)
        {
            return;
        }

        var accessPolicy = scope.ServiceProvider.GetRequiredService<IAccessPolicy>();
        if (!await accessPolicy.CanAsync(user.Id, attachment.SpaceId, ResourceKind.Notes, AccessLevel.Read, ct))
        {
            return;
        }

        var url = await attachmentService.GetReadUrlAsync(attachment.Id, TimeSpan.FromMinutes(5), ct);
        if (url is not null)
        {
            await channel.SendPhotoAsync(address, url, attachment.FileName, ct);
        }
    }

    // Shared by the native /note command and the create_note L3 tool — both create-and-confirm
    // the same way, undo button included (docs/10-conversazione.md).
    private async Task<string?> CreateNoteAndReplyAsync(
        NoteService notes, UndoService undo, OnboardingService onboarding, ChannelAddress address,
        Guid spaceId, Guid userId, string? title, string body, CancellationToken ct)
    {
        var note = await notes.CreateAsync(spaceId, userId, title, body, ct);
        await undo.RecordNoteAsync(userId, spaceId, note.Id, ct);
        await FinalizeUsefulActionReplyAsync(onboarding, address, userId, "notes", localizer["Notes.Created"].Value, ct);
        return null;
    }

    private async Task<string?> HandleLlmDeleteNoteAsync(
        AsyncServiceScope scope, NoteService notes, Guid spaceId, Guid userId, string searchText, CancellationToken ct)
    {
        var note = await notes.FindNoteAsync(spaceId, userId, searchText, ct);
        if (note is null)
        {
            return localizer["Notes.NotFound"];
        }

        await notes.DeleteAsync(spaceId, userId, note.Id, ct);
        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachments is not null)
        {
            await attachments.DeleteAllForAsync(ResourceKind.Notes, note.Id, ct);
        }

        return localizer["Notes.Deleted"];
    }

    // The only entry point into the attachment pipeline from the bot side (docs/06-roadmap.md
    // Fase 4) — a captioned photo/document becomes a new note with that attachment; an
    // uncaptioned one attaches to whatever note this user touched most recently, mirroring
    // UndoService's "most recent thing" idea but scoped to Notes since nothing else can take an
    // attachment yet.
    private async Task<string?> HandleIncomingMediaAsync(
        AsyncServiceScope scope, NoteService notes, ChannelAddress address, Guid spaceId, User user, string? caption, InboundMedia media, CancellationToken ct)
    {
        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachments is null)
        {
            return localizer["Attachments.NotConfigured"];
        }

        Note? note;
        var isNewNote = !string.IsNullOrWhiteSpace(caption);
        if (isNewNote)
        {
            note = await notes.CreateAsync(spaceId, user.Id, title: null, body: caption!, ct);
        }
        else
        {
            note = await notes.GetMostRecentByUserAsync(spaceId, user.Id, ct);
            if (note is null)
            {
                return localizer["Attachments.NoRecentNote"];
            }
        }

        using var content = await channel.DownloadMediaAsync(media.FileId, ct);
        var fileName = media.FileName ?? $"{media.Kind}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var contentType = media.MimeType ?? (media.Kind == "photo" ? "image/jpeg" : "application/octet-stream");
        await attachments.AddAsync(spaceId, ResourceKind.Notes, note.Id, user.Id, content, fileName, contentType, content.Length, ct);

        return isNewNote ? localizer["Attachments.NoteCreated"] : localizer["Attachments.AddedToRecentNote"];
    }

    // A photo captioned "/expense" (docs/06-roadmap.md Fase 4: "scontrini via vision") — reads
    // the receipt, then records the expense exactly like RecordExpenseAndReplyAsync's own
    // known-merchant branch (same resx strings, same categorization/budget/undo/notification
    // behavior), so a scanned receipt and a typed "/expense 12.50 at Conad" are indistinguishable
    // downstream. Gated on the same daily allowance as L3 (UsageService) — both are "the app
    // pays a model to do this," and a second parallel quota would just be more to explain.
    private async Task<string?> HandleReceiptAsync(
        AsyncServiceScope scope, ShoppingListService shopping, ExpenseService expenses, BudgetService budgets,
        NotificationService notifications, UndoService undo, OnboardingService onboarding, UsageService usage,
        ChannelAddress address, Guid spaceId, User user, CultureInfo culture, InboundMedia media, CancellationToken ct)
    {
        var vision = scope.ServiceProvider.GetService<ReceiptVisionClient>();
        if (vision is null)
        {
            return localizer["Expenses.ReceiptNotConfigured"];
        }

        // Checked before spending anything on the vision call, not after — a real per-scan
        // cost (docs/04-costi.md) only makes sense to pay for a paying space.
        var (_, _, plan) = await usage.GetTodayUsageAsync(spaceId, ct);
        if (!plan.AllowsReceiptScanning)
        {
            return localizer["Expenses.ReceiptRequiresPaidPlan"];
        }

        if (!await usage.TryRecordL3CallAsync(spaceId, ct))
        {
            return localizer["Usage.LimitExceeded"];
        }

        byte[] bytes;
        using (var content = await channel.DownloadMediaAsync(media.FileId, ct))
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var contentType = media.MimeType ?? "image/jpeg";
        var extraction = await vision.ExtractAsync(BinaryData.FromBytes(bytes), contentType, ct);
        if (extraction is null || extraction.Total <= 0)
        {
            return localizer["Expenses.ReceiptNotRecognized"];
        }

        var today = GetUserToday(user);

        // Stored so the products bought are searchable later (docs/06-roadmap.md Fase 4:
        // "archivio garanzie" — "quando ho comprato la lavatrice?") — QueryExpenseHistory
        // already matches search_text against Merchant or Note, so this is the entire
        // connection needed; no new query surface.
        var receiptNote = extraction.Items.Count > 0 ? string.Join(", ", extraction.Items) : null;

        Expense expense;
        string reply;
        if (extraction.Merchant is null)
        {
            expense = await expenses.RecordAsync(spaceId, user.Id, extraction.Total, categoryId: null, merchant: null, today, receiptNote, ct);
            reply = localizer["Expenses.Recorded", MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name)];
        }
        else
        {
            var learnedCategory = await expenses.FindMerchantCategoryAsync(spaceId, extraction.Merchant, ct);
            expense = await expenses.RecordAsync(spaceId, user.Id, extraction.Total, learnedCategory?.Id, extraction.Merchant, today, receiptNote, ct);
            var formatted = MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name);
            if (learnedCategory is not null)
            {
                reply = localizer["Expenses.RecordedWithMerchantAndCategory", formatted, extraction.Merchant, GetCategoryDisplayName(learnedCategory, localizer)];
            }
            else
            {
                await SendCategoryPickerAsync(expenses, address, spaceId, expense.Id, extraction.Merchant, ct);
                reply = localizer["Expenses.RecordedWithMerchant", formatted, extraction.Merchant];
            }
        }

        await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);

        var attachments = scope.ServiceProvider.GetService<AttachmentService>();
        if (attachments is not null)
        {
            var fileName = media.FileName ?? $"receipt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            using var receiptStream = new MemoryStream(bytes);
            await attachments.AddAsync(spaceId, ResourceKind.Expenses, expense.Id, user.Id, receiptStream, fileName, contentType, bytes.Length, ct);
        }

        // Matched against the shopping list the same way a typed "check off the milk" would be
        // (docs/06-roadmap.md Fase 4: "un gesto, due sistemi") — reuses CheckItemAsync's own
        // fuzzy match rather than inventing a second one. Notified like any other check, but
        // doesn't touch the undo slot: LastOperation holds one operation per user, not a stack,
        // and the expense this receipt also created is the more consequential thing to be able
        // to undo — recorded right after this, so it keeps the slot.
        var checkedItemNames = new List<string>();
        foreach (var itemName in extraction.Items)
        {
            var checkedItem = await shopping.CheckItemAsync(spaceId, user.Id, itemName, listName: null, ct);
            if (checkedItem is null)
            {
                continue;
            }

            checkedItemNames.Add(checkedItem.RawText);
            await notifications.NotifyAsync(
                new ShoppingItemChecked(spaceId, user.Id, checkedItem.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        }

        if (checkedItemNames.Count > 0)
        {
            reply = $"{reply}\n{localizer["Shopping.CheckedFromReceipt", string.Join(", ", checkedItemNames)]}";
        }

        await undo.RecordExpenseAsync(user.Id, spaceId, expense.Id, ct);
        var replyWithAlerts = await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, expense, reply, ct);
        await FinalizeUsefulActionReplyAsync(onboarding, address, user.Id, "expenses", replyWithAlerts, ct);
        return null;
    }

    private string GetFrequencyDisplayName(RecurrenceFrequency frequency) => frequency switch
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

    private async Task<string?> HandleRecurringCommandAsync(
        RecurringExpenseService recurringExpenses, Guid spaceId, User user, CultureInfo culture,
        string argsText, CancellationToken ct)
    {
        var command = RecurringExpenseCommandParser.Parse(argsText);
        if (command is null)
        {
            return localizer["RecurringExpenses.Usage"];
        }

        switch (command)
        {
            case RecurringExpenseCommand.ListActive:
                return await HandleRecurringListAsync(recurringExpenses, spaceId, user.Id, culture, ct);

            case RecurringExpenseCommand.Create create:
            {
                if (!decimal.TryParse(create.AmountText, NumberStyles.Number, culture, out var amount))
                {
                    return localizer["RecurringExpenses.InvalidAmount", create.AmountText];
                }

                var recurring = await recurringExpenses.CreateAsync(
                    spaceId, user.Id, amount, create.Description, create.Frequency, create.AutoRegister, ct);
                var formatted = MoneyFormatter.Format(recurring.Amount, recurring.Currency, culture.Name);
                var frequencyName = GetFrequencyDisplayName(create.Frequency);
                return create.AutoRegister
                    ? localizer["RecurringExpenses.CreatedAutoRegister", recurring.Description, formatted, frequencyName]
                    : localizer["RecurringExpenses.CreatedReminderOnly", recurring.Description, formatted, frequencyName];
            }

            default:
                return null;
        }
    }

    private async Task<string?> HandleRecurringListAsync(
        RecurringExpenseService recurringExpenses, Guid spaceId, Guid userId, CultureInfo culture, CancellationToken ct)
    {
        var active = await recurringExpenses.GetActiveAsync(spaceId, userId, ct);
        if (active.Count == 0)
        {
            return localizer["RecurringExpenses.ListEmpty"];
        }

        var lines = active.Select(x =>
        {
            var formatted = MoneyFormatter.Format(x.Amount, x.Currency, culture.Name);
            var frequencyName = GetFrequencyDisplayName(x.Recurrence.Frequency);
            return x.AutoRegister
                ? localizer["RecurringExpenses.ListItemLineAuto", x.Description, formatted, frequencyName].Value
                : localizer["RecurringExpenses.ListItemLineReminderOnly", x.Description, formatted, frequencyName].Value;
        });
        return string.Join('\n', lines);
    }

    private async Task<string?> HandleBudgetCommandAsync(
        ExpenseService expenses, BudgetService budgets, Guid spaceId, User user, CultureInfo culture,
        string argsText, CancellationToken ct)
    {
        var command = BudgetCommandParser.Parse(argsText);
        if (command is null)
        {
            return localizer["Budget.Usage"];
        }

        switch (command)
        {
            case BudgetCommand.ListActive:
                return await HandleBudgetListAsync(expenses, budgets, spaceId, user.Id, culture, ct);

            case BudgetCommand.SetOverall setOverall:
            {
                if (!decimal.TryParse(setOverall.AmountText, NumberStyles.Number, culture, out var amount))
                {
                    return localizer["Budget.InvalidAmount", setOverall.AmountText];
                }

                var budget = await budgets.SetAsync(spaceId, user.Id, categoryId: null, amount, ct);
                var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);
                return localizer["Budget.SetOverall", MoneyFormatter.Format(budget.MonthlyLimit, currency, culture.Name)];
            }

            case BudgetCommand.SetCategory setCategory:
            {
                if (!decimal.TryParse(setCategory.AmountText, NumberStyles.Number, culture, out var amount))
                {
                    return localizer["Budget.InvalidAmount", setCategory.AmountText];
                }

                var category = await ResolveCategoryAsync(expenses, spaceId, setCategory.CategoryText, ct);
                if (category is null)
                {
                    return localizer["Expenses.CategoryNotFound", setCategory.CategoryText];
                }

                var budget = await budgets.SetAsync(spaceId, user.Id, category.Id, amount, ct);
                var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);
                return localizer["Budget.SetCategory",
                    GetCategoryDisplayName(category, localizer), MoneyFormatter.Format(budget.MonthlyLimit, currency, culture.Name)];
            }

            default:
                return null;
        }
    }

    private async Task<string> HandleBudgetListAsync(
        ExpenseService expenses, BudgetService budgets, Guid spaceId, Guid userId, CultureInfo culture, CancellationToken ct)
    {
        var active = await budgets.GetActiveAsync(spaceId, userId, ct);
        if (active.Count == 0)
        {
            return localizer["Budget.ListEmpty"];
        }

        var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);
        var categories = await expenses.GetCategoriesAsync(spaceId, ct);

        var lines = active.Select(b =>
        {
            var limitFormatted = MoneyFormatter.Format(b.MonthlyLimit, currency, culture.Name);
            if (b.CategoryId is null)
            {
                return localizer["Budget.ListItemLineOverall", limitFormatted].Value;
            }

            var category = categories.FirstOrDefault(c => c.Id == b.CategoryId);
            var categoryName = category is null ? "" : GetCategoryDisplayName(category, localizer);
            return localizer["Budget.ListItemLineCategory", categoryName, limitFormatted].Value;
        });
        return string.Join('\n', lines);
    }

    private async Task<string> HandleDigestCommandAsync(
        DigestService digest, ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, CancellationToken ct)
    {
        var timeZoneId = user.TimeZoneId ?? "UTC";
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var today = GetUserToday(user);

        var daily = await digest.BuildAsync(spaceId, user.Id, timeZone, today, ct);
        var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);
        var categories = await expenses.GetCategoriesAsync(spaceId, ct);

        return DigestFormatter.Format(daily, categories, currency, timeZone, culture, localizer);
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
    // of truth, so the menu and /help can't drift apart (docs/09-localizzazione.md).
    private string HandleHelpCommand() => string.Join('\n', [
        $"/list — {localizer["Commands.List.Description"]}",
        $"/expense — {localizer["Commands.Expense.Description"]}",
        $"/remind — {localizer["Commands.Remind.Description"]}",
        $"/note — {localizer["Commands.Note.Description"]}",
        $"/usage — {localizer["Commands.Usage.Description"]}",
        $"/month — {localizer["Commands.Month.Description"]}",
        $"/link — {localizer["Commands.Link.Description"]}",
        $"/language — {localizer["Commands.Language.Description"]}",
        $"/help — {localizer["Commands.Help.Description"]}",
    ]);
}
