using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Tessera.Ai.Commands;
using Tessera.Ai.Routing;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Conversations;
using Tessera.Core.Expenses;
using Tessera.Core.Notifications;
using Tessera.Core.Reminders;
using Tessera.Core.Resources;
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
    ILogger<MessageProcessor> logger) : BackgroundService
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
                logger.LogError(ex,
                    "Failed to process message {ChannelName}/{ProviderMessageId}",
                    message.ChannelName, message.ProviderMessageId);
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

        if (message.CallbackData is { } cd)
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

        var spaces = scope.ServiceProvider.GetRequiredService<SpaceResolver>();
        var resolution = await spaces.ResolveAsync(user.Id, resourceKind, requiredLevel, text, ct);

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

        if (message.CallbackData is { } callbackData)
        {
            // L1 (docs/05-ottimizzazioni.md): an inline-keyboard tap is already a
            // structured action — it never goes through the intent matcher.
            await HandleCallbackAsync(shopping, expenses, reminders, budgets, notifications, address, spaceId, user, culture, callbackData, ct);
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
                    reminders, address, spaceId, user, culture, text["/remind".Length..], ct);
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

            case NativeCommand.List:
            {
                var listReply = await HandleShowAsync(shopping, address, spaceId, user.Id, ct);
                if (listReply is not null)
                {
                    await channel.SendTextAsync(address, listReply, ct);
                }
                return;
            }

            case NativeCommand.Expense:
            {
                var expenseReply = await HandleExpenseCommandAsync(
                    expenses, budgets, notifications, address, spaceId, user, culture, text["/expense".Length..], ct);
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
        }

        string? reply;
        if (match is null)
        {
            // No L3/LLM fallback wired up yet — this is the honest answer until it is.
            reply = localizer["Errors.NotUnderstood"];
        }
        else
        {
            reply = match.Intent switch
            {
                "shopping.add" => await HandleAddAsync(shopping, notifications, address, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.show" => await HandleShowAsync(shopping, address, spaceId, user.Id, ct),
                "shopping.check" => await HandleCheckAsync(shopping, notifications, address, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.remove" => await HandleRemoveAsync(shopping, spaceId, user.Id, match.Slots["item"], ct),
                "shopping.clear" => await HandleClearAsync(shopping, spaceId, user.Id, ct),
                "expenses.add" => await HandleExpenseAddAsync(
                    expenses, budgets, notifications, address, spaceId, user, culture, match.Slots["amount"],
                    match.Slots.GetValueOrDefault("category"), match.Slots.GetValueOrDefault("merchant"), ct),
                "expenses.query" => await HandleExpensesQueryAsync(expenses, spaceId, user, culture, ct),
                "expenses.query.category" => await HandleExpensesQueryByCategoryAsync(
                    expenses, spaceId, user, culture, match.Slots["category"], ct),
                // Recognized as a reminder attempt, but the date itself still needs L3
                // (docs/05-ottimizzazioni.md) — point at the syntax that works today
                // instead of the generic "I didn't get that".
                "reminders.natural" => localizer["Reminders.Usage"],
                _ => null,
            };
        }

        if (reply is not null)
        {
            await channel.SendTextAsync(address, reply, ct);
        }
    }

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
        _ => (ResourceKind.ShoppingList, AccessLevel.Read),
    };

    private static (ResourceKind, AccessLevel) ResourceForIntent(string intent) => intent switch
    {
        "shopping.add" or "shopping.check" or "shopping.remove" or "shopping.clear" => (ResourceKind.ShoppingList, AccessLevel.Write),
        "shopping.show" => (ResourceKind.ShoppingList, AccessLevel.Read),
        "expenses.add" => (ResourceKind.Expenses, AccessLevel.Write),
        "expenses.query" or "expenses.query.category" => (ResourceKind.Expenses, AccessLevel.Read),
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

    private async Task HandleCallbackAsync(
        ShoppingListService shopping, ExpenseService expenses, ReminderService reminders, BudgetService budgets,
        NotificationService notifications, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        string callbackData, CancellationToken ct)
    {
        var parts = callbackData.Split(':');

        if (parts.Length == 2 && parts[0] == "shopping.check" && Guid.TryParse(parts[1], out var itemId))
        {
            await HandleShoppingCheckCallbackAsync(shopping, notifications, address, spaceId, user.Id, itemId, ct);
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
            await HandleExpenseConfirmCallbackAsync(expenses, budgets, notifications, address, spaceId, user, culture, pendingId, parts[2], ct);
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
        ShoppingListService shopping, NotificationService notifications, ChannelAddress address, Guid spaceId,
        Guid userId, Guid itemId, CancellationToken ct)
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
        await channel.SendTextAsync(address, localizer["Shopping.ItemChecked", item.RawText], ct);
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

        var reply = linkedUser is null
            ? localizer["Link.Invalid"]
            : localizer["Link.Success", linkedUser.DisplayName ?? linkedUser.Email];

        logger.LogInformation("Link attempt for {ChannelName} identity {ExternalUserId}: {Result}",
            message.ChannelName, message.ExternalUserId, linkedUser is null ? "invalid/expired" : "success");

        var address = new ChannelAddress(message.ChannelName, message.ExternalChatId);
        await channel.SendTextAsync(address, reply, ct);
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

    private async Task<string> HandleAddAsync(
        ShoppingListService shopping, NotificationService notifications, ChannelAddress address, Guid spaceId,
        Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.AddItemAsync(spaceId, userId, itemText, ct);
        await notifications.NotifyAsync(
            new ShoppingItemAdded(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        return localizer["Shopping.ItemAdded", item.RawText];
    }

    private async Task<string?> HandleShowAsync(
        ShoppingListService shopping, ChannelAddress address, Guid spaceId, Guid userId, CancellationToken ct)
    {
        var items = await shopping.GetItemsAsync(spaceId, userId, ct);
        if (items.Count == 0)
        {
            return localizer["Shopping.ListEmpty"];
        }

        var lines = items.Select(i => localizer[
            i.IsChecked ? "Shopping.ListItemLineChecked" : "Shopping.ListItemLine", i.RawText].Value);
        var text = string.Join('\n', lines);

        // One button per unchecked item — a tap is a callback_query, zero interpretation,
        // zero LLM cost (docs/03-integrazioni.md, docs/05-ottimizzazioni.md).
        var choices = items
            .Where(i => !i.IsChecked)
            .Select(i => new Choice(i.RawText, $"shopping.check:{i.Id}"))
            .ToList();

        if (choices.Count == 0)
        {
            await channel.SendTextAsync(address, text, ct);
        }
        else
        {
            await channel.SendChoicesAsync(address, text, choices, ct);
        }

        return null;
    }

    private async Task<string> HandleCheckAsync(
        ShoppingListService shopping, NotificationService notifications, ChannelAddress address, Guid spaceId,
        Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.CheckItemAsync(spaceId, userId, itemText, ct);
        if (item is null)
        {
            return localizer["Shopping.ItemNotFound", itemText];
        }

        await notifications.NotifyAsync(
            new ShoppingItemChecked(spaceId, userId, item.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        return localizer["Shopping.ItemChecked", item.RawText];
    }

    private async Task<string> HandleRemoveAsync(
        ShoppingListService shopping, Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        var item = await shopping.RemoveItemAsync(spaceId, userId, itemText, ct);
        return item is null
            ? localizer["Shopping.ItemNotFound", itemText]
            : localizer["Shopping.ItemRemoved", item.RawText];
    }

    private async Task<string> HandleClearAsync(ShoppingListService shopping, Guid spaceId, Guid userId, CancellationToken ct)
    {
        await shopping.ClearAsync(spaceId, userId, ct);
        return localizer["Shopping.ListCleared"];
    }

    private async Task<string?> HandleExpenseAddAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, string amountText, string? categoryText, string? merchantText,
        CancellationToken ct)
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
            expenses, budgets, notifications, address, spaceId, user, culture, amount, categoryText, merchantText, ct);
    }

    // The trivial form of the /expense menu command — amount plus an optional free-text
    // category, no merchant slot (that's the natural-language path's job).
    private async Task<string?> HandleExpenseCommandAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, string argsText, CancellationToken ct)
    {
        var command = ExpenseCommandParser.Parse(argsText);
        if (command is null)
        {
            return localizer["Expenses.Usage"];
        }

        return await HandleExpenseAddAsync(expenses, budgets, notifications, address, spaceId, user, culture,
            command.AmountText, command.CategoryText, merchantText: null, ct);
    }

    private async Task HandleExpenseConfirmCallbackAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, Guid pendingId, string choice, CancellationToken ct)
    {
        var pending = await expenses.ConsumePendingConfirmationAsync(spaceId, pendingId, ct);
        if (pending is null)
        {
            // Expired, or already resolved by a previous tap — the button is stale.
            return;
        }

        var amount = choice == "g" ? pending.CandidateAsGrouped : pending.CandidateAsDecimal;
        var reply = await RecordExpenseAndReplyAsync(
            expenses, budgets, notifications, address, spaceId, user, culture, amount, pending.CategoryText, pending.MerchantText, ct);
        if (reply is not null)
        {
            await channel.SendTextAsync(address, reply, ct);
        }
    }

    // Shared by the direct (unambiguous) path and the post-confirmation path, so recording
    // and the categorization precedence (docs/02-modello-dati.md) can't drift between them.
    private async Task<string?> RecordExpenseAndReplyAsync(
        ExpenseService expenses, BudgetService budgets, NotificationService notifications, ChannelAddress address,
        Guid spaceId, User user, CultureInfo culture, decimal amount, string? categoryText, string? merchantText,
        CancellationToken ct)
    {
        var today = GetUserToday(user);

        // An explicit category always wins — there is nothing to resolve or learn.
        if (categoryText is not null)
        {
            var category = await ResolveCategoryAsync(expenses, spaceId, categoryText, ct);
            var expense = await expenses.RecordAsync(spaceId, user.Id, amount, category?.Id, merchant: null, today, ct);
            await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);
            var formatted = MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name);
            var reply = category is null
                ? localizer["Expenses.Recorded", formatted]
                : localizer["Expenses.RecordedWithCategory", formatted, GetCategoryDisplayName(category, localizer)];
            return await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, expense, reply, ct);
        }

        // No merchant either: nothing to categorize, nothing to learn from.
        if (merchantText is null)
        {
            var expense = await expenses.RecordAsync(spaceId, user.Id, amount, categoryId: null, merchant: null, today, ct);
            await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, expense, address, ct);
            var reply = localizer["Expenses.Recorded", MoneyFormatter.Format(expense.Amount, expense.Currency, culture.Name)];
            return await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, expense, reply, ct);
        }

        // Categorization strategy, in order of precedence (docs/02-modello-dati.md):
        // 1. learned merchant → category mapping, applied silently;
        // 4. unknown merchant → ask once via inline keyboard, and the answer feeds back
        //    into the mapping so this merchant is never asked about again.
        var learnedCategory = await expenses.FindMerchantCategoryAsync(spaceId, merchantText, ct);
        var recorded = await expenses.RecordAsync(spaceId, user.Id, amount, learnedCategory?.Id, merchantText, today, ct);
        await NotifyExpenseRecordedAsync(notifications, spaceId, user.Id, recorded, address, ct);
        var recordedFormatted = MoneyFormatter.Format(recorded.Amount, recorded.Currency, culture.Name);

        if (learnedCategory is not null)
        {
            var reply = localizer["Expenses.RecordedWithMerchantAndCategory",
                recordedFormatted, merchantText, GetCategoryDisplayName(learnedCategory, localizer)];
            return await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, recorded, reply, ct);
        }

        await SendCategoryPickerAsync(expenses, address, spaceId, recorded.Id, merchantText, ct);
        // No category yet — nothing to check against a per-category budget, only the overall one.
        return await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, recorded,
            localizer["Expenses.RecordedWithMerchant", recordedFormatted, merchantText], ct);
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
        // Specific months ("a gennaio") aren't parsed yet — date phrases go to the LLM
        // fallback (docs/05-ottimizzazioni.md), which isn't wired up. This always answers
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
        ReminderService reminders, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        string argsText, CancellationToken ct)
    {
        var command = RemindCommandParser.Parse(argsText);
        if (command is null)
        {
            // Not one of the trivial forms — natural language dates aren't parsed without
            // an LLM fallback (docs/05-ottimizzazioni.md), so this is the honest answer.
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
                return localizer["Reminders.CreatedOnce", FormatDueAt(reminder.DueAt, timeZone, culture)];
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
                return localizer["Reminders.CreatedRecurring",
                    GetFrequencyDisplayName(recurring.Frequency), FormatDueAt(reminder.DueAt, timeZone, culture)];
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
        $"/month — {localizer["Commands.Month.Description"]}",
        $"/link — {localizer["Commands.Link.Description"]}",
        $"/language — {localizer["Commands.Language.Description"]}",
        $"/help — {localizer["Commands.Help.Description"]}",
    ]);
}
