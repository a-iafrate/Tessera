using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Ai.Commands;
using Tessera.Ai.Llm;
using Tessera.Core.Channels;
using Tessera.Core.Expenses;
using Tessera.Core.Notifications;
using Tessera.Core.Reminders;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;

namespace Tessera.Web.Services;

// Second of five domain handler classes to be extracted out of MessageProcessor
// (docs/13-piano-miglioramenti.md, F3). Unlike Shopping, this one bundles Budget, Recurring
// expenses and the /digest command alongside Expenses itself — the plan lists five handler
// classes, not eight, and none of those three has anywhere else to go: budgets are expense
// limits, recurring expenses generate expenses, and /digest's own MessageProcessor method took
// only DigestService and ExpenseService, nothing from the other three domains.
//
// Constructed fresh per message inside MessageProcessor.ProcessAsync, not DI-registered as a
// singleton — same reasoning as ShoppingHandlers: MessageProcessor.channel is a mutable field
// only a per-message instance can safely capture.
public sealed class ExpenseHandlers(
    IChannel channel,
    IStringLocalizer<Messages> localizer,
    Func<OnboardingService, ChannelAddress, Guid, string, string, CancellationToken, Task> finalizeReplyAsync)
{
    public async Task<string?> HandleExpenseAddAsync(
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
    public async Task<string?> HandleExpenseCommandAsync(
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

    public async Task HandleExpenseConfirmCallbackAsync(
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
    public async Task<string?> RecordExpenseAndReplyAsync(
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
        await finalizeReplyAsync(onboarding, address, user.Id, "expenses", replyWithAlerts, ct);
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

    // Proposed once, right when the qualifying line is recorded (docs/13-piano-miglioramenti.md,
    // E2) — there's no separate "already asked" state to track, since this only ever runs once
    // per receipt scan, synchronously, not on a recurring scan like CalendarToListSuggestionJob.
    // Picks the single highest-priced qualifying line if several clear the threshold, rather
    // than proposing a reminder per line.
    private async Task ProposeWarrantyReminderAsync(ChannelAddress address, IReadOnlyList<ExpenseLine> lines, CancellationToken ct)
    {
        var candidate = lines
            .Where(l => l.Price >= WarrantyReminderDefaults.ThresholdAmount)
            .OrderByDescending(l => l.Price)
            .FirstOrDefault();
        if (candidate is null)
        {
            return;
        }

        var choices = new[]
        {
            new Choice(localizer["Expenses.WarrantyReminderYes"].Value, $"warranty:{candidate.Id}:yes"),
            new Choice(localizer["Expenses.WarrantyReminderNo"].Value, $"warranty:{candidate.Id}:no"),
        };
        await channel.SendChoicesAsync(address, localizer["Expenses.WarrantyReminderPrompt", candidate.RawText], choices, ct);
    }

    public async Task<string> HandleExpensesQueryAsync(
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

    public async Task<string> HandleExpensesQueryByCategoryAsync(
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
    public async Task<string> HandleHistoryQueryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var aggregationText = args.GetProperty("aggregation").GetString();
        if (!Enum.TryParse<HistoryAggregation>(aggregationText, ignoreCase: true, out var aggregation))
        {
            aggregation = HistoryAggregation.Total;
        }

        Guid? categoryId = null;
        if (MessageProcessor.GetOptionalString(args, "category") is { } categoryText)
        {
            categoryId = (await ResolveCategoryAsync(expenses, spaceId, categoryText, ct))?.Id;
        }

        var dateFrom = ParseOptionalDate(MessageProcessor.GetOptionalString(args, "date_from"));
        var dateTo = ParseOptionalDate(MessageProcessor.GetOptionalString(args, "date_to"));
        var searchText = MessageProcessor.GetOptionalString(args, "search_text");

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

    // "Storico prezzi" (docs/06-roadmap.md) — built entirely from ExpenseLine rows recorded
    // by receipt scanning, so an empty result is the normal case for a space that hasn't
    // scanned a receipt with that product yet, not an error.
    public async Task<string> HandlePriceHistoryQueryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var product = args.GetProperty("product").GetString() ?? "";
        var compareDate = ParseOptionalDate(MessageProcessor.GetOptionalString(args, "compare_to_date"));

        var result = await expenses.QueryPriceHistoryAsync(spaceId, user.Id, product, compareDate, ct);
        if (result.ObservationCount == 0)
        {
            return localizer["PriceHistory.NotFound", product];
        }

        var latestFormatted = MoneyFormatter.Format(result.LatestPrice!.Value, result.Currency, culture.Name);
        if (result.LatestDate == result.ComparisonDate)
        {
            return localizer["PriceHistory.SingleObservation", product, latestFormatted, result.LatestDate!.Value.ToString("d MMMM yyyy", culture)];
        }

        var comparisonDateText = result.ComparisonDate!.Value.ToString("d MMMM yyyy", culture);
        if (result.ComparisonPrice is null or 0)
        {
            return localizer["PriceHistory.SingleObservation", product, latestFormatted, result.LatestDate!.Value.ToString("d MMMM yyyy", culture)];
        }

        var percentChange = Math.Round((result.LatestPrice!.Value - result.ComparisonPrice.Value) / result.ComparisonPrice.Value * 100, 0);
        return percentChange switch
        {
            0 => localizer["PriceHistory.Unchanged", product, latestFormatted, comparisonDateText],
            > 0 => localizer["PriceHistory.Increased", product, latestFormatted, percentChange, comparisonDateText],
            _ => localizer["PriceHistory.Decreased", product, latestFormatted, Math.Abs(percentChange), comparisonDateText],
        };
    }

    // "Dove conviene comprare X" (docs/13-piano-miglioramenti.md, E6) — a distinct question
    // shape from HandlePriceHistoryQueryAsync ("has the price changed over time"): this compares
    // merchants against each other at (roughly) the same point in time, cheapest first.
    public async Task<string> HandlePriceByMerchantQueryAsync(
        ExpenseService expenses, Guid spaceId, User user, CultureInfo culture, JsonElement args, CancellationToken ct)
    {
        var product = args.GetProperty("product").GetString() ?? "";
        var observations = await expenses.QueryPriceByMerchantAsync(spaceId, user.Id, product, ct);
        if (observations.Count == 0)
        {
            return localizer["PriceHistory.NotFound", product];
        }

        var currency = await expenses.GetSpaceCurrencyAsync(spaceId, ct);
        if (observations.Count == 1)
        {
            var only = observations[0];
            return localizer["PriceByMerchant.SingleMerchant", product, only.Merchant, MoneyFormatter.Format(only.Price, currency, culture.Name)];
        }

        var cheapest = observations[0];
        var header = localizer["PriceByMerchant.Comparison", product, cheapest.Merchant, MoneyFormatter.Format(cheapest.Price, currency, culture.Name)].Value;
        var lines = observations.Select(o => localizer["PriceByMerchant.Line", o.Merchant, MoneyFormatter.Format(o.Price, currency, culture.Name)].Value);
        return header + "\n\n" + string.Join('\n', lines);
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
    // Internal static so DigestFormatter/ExpenseCsvExporter/the console pages can reuse it
    // without an ExpenseHandlers instance.
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

    public async Task HandleExpenseCategorizeCallbackAsync(
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

    public async Task HandleWarrantyReminderCallbackAsync(
        ExpenseService expenses, ReminderService reminders, ChannelAddress address, Guid spaceId, User user, CultureInfo culture,
        Guid lineId, string choice, CancellationToken ct)
    {
        if (choice != "yes")
        {
            await channel.SendTextAsync(address, localizer["Expenses.WarrantyReminderDismissed"], ct);
            return;
        }

        var found = await expenses.GetLineWithExpenseAsync(spaceId, lineId, ct);
        if (found is null)
        {
            // Wrong space, or the expense/line is gone — the button is stale.
            return;
        }

        var (line, expense) = found.Value;
        var timeZoneId = user.TimeZoneId ?? "UTC";
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var dueDate = expense.Date.AddMonths(WarrantyReminderDefaults.MonthsAhead);
        var localDateTime = dueDate.ToDateTime(new TimeOnly(9, 0));
        var dueAt = new DateTimeOffset(localDateTime, timeZone.GetUtcOffset(localDateTime));

        var reminder = await reminders.CreateOnceAsync(
            spaceId, user.Id, localizer["Expenses.WarrantyReminderText", line.RawText].Value, dueAt, timeZoneId, ct);
        await channel.SendTextAsync(address, localizer["Expenses.WarrantyReminderConfirmed", MessageProcessor.FormatDueAt(reminder.DueAt, timeZone, culture)], ct);
    }

    // A photo captioned "/expense" (docs/06-roadmap.md Fase 4: "scontrini via vision") — reads
    // the receipt, then records the expense exactly like RecordExpenseAndReplyAsync's own
    // known-merchant branch (same resx strings, same categorization/budget/undo/notification
    // behavior), so a scanned receipt and a typed "/expense 12.50 at Conad" are indistinguishable
    // downstream. Gated on the same daily allowance as L3 (UsageService) — both are "the app
    // pays a model to do this," and a second parallel quota would just be more to explain.
    public async Task<string?> HandleReceiptAsync(
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
        // cost (docs/04-costi.md) only makes sense to pay for once the free monthly allowance
        // is used up (docs/13-piano-miglioramenti.md, D1 — replaces the old all-or-nothing
        // AllowsReceiptScanning bool).
        if (!await usage.TryRecordReceiptScanAsync(spaceId, ct))
        {
            return localizer["Expenses.ReceiptLimitExceeded"];
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
        var receiptNote = extraction.Items.Count > 0 ? string.Join(", ", extraction.Items.Select(i => i.Name)) : null;

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
        foreach (var item in extraction.Items)
        {
            var checkedItem = await shopping.CheckItemAsync(spaceId, user.Id, item.Name, listName: null, ct);
            if (checkedItem is null)
            {
                continue;
            }

            checkedItemNames.Add(checkedItem.RawText);
            await notifications.NotifyAsync(
                new ShoppingItemChecked(spaceId, user.Id, checkedItem.RawText, address.ExternalChatId, DateTimeOffset.UtcNow), ct);
        }

        // Storico prezzi (docs/06-roadmap.md) — only lines with a legible price feed price
        // history; the others still checked off the list and landed in Note above.
        var pricedItems = extraction.Items
            .Where(i => i.Price is > 0)
            .Select(i => (i.Name, Price: i.Price!.Value))
            .ToList();
        IReadOnlyList<ExpenseLine> lines = [];
        if (pricedItems.Count > 0)
        {
            lines = await expenses.AddLinesAsync(expense.Id, pricedItems, ct);
        }

        if (checkedItemNames.Count > 0)
        {
            reply = $"{reply}\n{localizer["Shopping.CheckedFromReceipt", string.Join(", ", checkedItemNames)]}";
        }

        await undo.RecordExpenseAsync(user.Id, spaceId, expense.Id, ct);
        var replyWithAlerts = await AppendBudgetAlertsAsync(expenses, budgets, spaceId, user.Id, culture, expense, reply, ct);
        await finalizeReplyAsync(onboarding, address, user.Id, "expenses", replyWithAlerts, ct);

        // Sent after the recorded-expense confirmation, not before — one novelty at a time
        // (docs/10-conversazione.md): the primary result lands first, the optional follow-up
        // proposal comes after.
        await ProposeWarrantyReminderAsync(address, lines, ct);
        return null;
    }

    public async Task<string?> HandleRecurringCommandAsync(
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
                var frequencyName = MessageProcessor.GetFrequencyDisplayName(create.Frequency, localizer);
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
            var frequencyName = MessageProcessor.GetFrequencyDisplayName(x.Recurrence.Frequency, localizer);
            return x.AutoRegister
                ? localizer["RecurringExpenses.ListItemLineAuto", x.Description, formatted, frequencyName].Value
                : localizer["RecurringExpenses.ListItemLineReminderOnly", x.Description, formatted, frequencyName].Value;
        });
        return string.Join('\n', lines);
    }

    public async Task<string?> HandleBudgetCommandAsync(
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

    public async Task<string> HandleDigestCommandAsync(
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
}
