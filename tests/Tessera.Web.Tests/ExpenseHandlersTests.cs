using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Tessera.Core.Abstractions;
using Tessera.Core.Channels;
using Tessera.Core.Expenses;
using Tessera.Core.Resources;
using Tessera.Core.Spaces;
using Tessera.Core.Users;
using Tessera.Data;
using Tessera.Web.Services;

namespace Tessera.Web.Tests;

// Second extracted domain handler (docs/13-piano-miglioramenti.md, F3) — bundles Expenses,
// Budget, Recurring expenses and /digest (see ExpenseHandlers.cs's own doc comment for why).
// Same testing shape as ShoppingHandlersTests: real domain services against an in-memory SQLite
// database, a hand-written FakeChannel, a real IStringLocalizer<Messages> reading actual .resx
// text. Doesn't re-test every branch RecordExpenseAndReplyAsync has (merchant learning,
// category picker, budget alerts) — those are exercised indirectly via HandleExpenseAddAsync
// and HandleReceiptAsync's own MessageProcessor-era manual testing; this covers each public
// method's contract once.
public class ExpenseHandlersTests : IDisposable
{
    private readonly TestWebDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly ExpenseService expenses;
    private readonly BudgetService budgets;
    private readonly RecurringExpenseService recurringExpenses;
    private readonly DigestService digest;
    private readonly NotificationService notifications;
    private readonly UndoService undo;
    private readonly OnboardingService onboarding;
    private readonly ReminderService reminders;
    private readonly ShoppingListService shopping;
    private readonly FakeChannel channel = new();
    private readonly IStringLocalizer<Messages> localizer;
    private readonly List<(Guid UserId, string FeatureKey, string BaseReply)> finalizedReplies = [];

    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid userId = Guid.NewGuid();
    private readonly User user;
    private readonly ChannelAddress address = new("fake", "chat-1");

    public ExpenseHandlersTests()
    {
        var accessPolicy = new TestWebDatabase.AllowAllAccessPolicy();
        expenses = new ExpenseService(Db, accessPolicy, testDb.Cache);
        budgets = new BudgetService(Db, accessPolicy, expenses);
        recurringExpenses = new RecurringExpenseService(Db, accessPolicy);
        reminders = new ReminderService(Db, accessPolicy);
        shopping = new ShoppingListService(Db, accessPolicy);
        digest = new DigestService(reminders, shopping, budgets, new ServiceCollection().BuildServiceProvider());
        undo = new UndoService(Db);
        onboarding = new OnboardingService(Db);
        localizer = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider().GetRequiredService<IStringLocalizer<Messages>>();
        notifications = new NotificationService(
            Db, new FakeChannelIdentityRepository(), new ActorNameResolver(Db), new FakeChannelRegistry(), new NotificationAggregator(), localizer);

        user = new User { Id = userId, Email = "a@example.com", PreferredCulture = "en" };
        Db.DomainUsers.Add(user);
        Db.Spaces.Add(new Space { Id = spaceId, Name = "Casa", OwnerId = userId, PlanId = SystemPlanIds.Free, CreatedAt = DateTimeOffset.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() => testDb.Dispose();

    private ExpenseHandlers CreateHandlers() => new(channel, localizer, (o, a, u, featureKey, baseReply, ct) =>
    {
        finalizedReplies.Add((u, featureKey, baseReply));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task HandleExpenseAddAsync_RecordsExpense_AndFinalizesReply()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleExpenseAddAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user,
            System.Globalization.CultureInfo.InvariantCulture, "12.50", categoryText: null, merchantText: null, CancellationToken.None);

        Assert.Null(result); // already sent via finalizeReplyAsync
        var recorded = Assert.Single(await expenses.GetRecentAsync(spaceId, userId, 10, CancellationToken.None));
        Assert.Equal(12.50m, recorded.Amount);
        var finalized = Assert.Single(finalizedReplies);
        Assert.Equal("expenses", finalized.FeatureKey);
        Assert.Equal($"Recorded {MoneyFormatter.Format(12.50m, "EUR", "en")}", finalized.BaseReply);
    }

    [Fact]
    public async Task HandleExpenseAddAsync_ReturnsError_WhenAmountIsUnparseable()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleExpenseAddAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user,
            System.Globalization.CultureInfo.InvariantCulture, "not-a-number", categoryText: null, merchantText: null, CancellationToken.None);

        Assert.Equal("I couldn't read \"not-a-number\" as an amount", result);
        Assert.Empty(await expenses.GetRecentAsync(spaceId, userId, 10, CancellationToken.None));
    }

    [Fact]
    public async Task HandleExpenseAddAsync_AsksToConfirm_WhenAmountIsAmbiguous()
    {
        var handlers = CreateHandlers();

        // Invariant culture: ',' is the group separator, '.' the decimal one — "1,500" has a
        // group separator and no decimal separator, so it's ambiguous (AmountAmbiguity.cs).
        var result = await handlers.HandleExpenseAddAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user,
            System.Globalization.CultureInfo.InvariantCulture, "1,500", categoryText: null, merchantText: null, CancellationToken.None);

        Assert.Null(result); // already sent as a choice, not recorded yet
        Assert.Empty(await expenses.GetRecentAsync(spaceId, userId, 10, CancellationToken.None));
        var sent = Assert.Single(channel.SentChoices);
        Assert.Equal("Which amount did you mean?", sent.Text);
        Assert.Equal(2, sent.Choices.Count);
    }

    [Fact]
    public async Task HandleExpenseConfirmCallbackAsync_RecordsTheChosenAmount()
    {
        var handlers = CreateHandlers();
        var pending = await expenses.CreatePendingConfirmationAsync(
            spaceId, userId, candidateAsGrouped: 1500m, candidateAsDecimal: 1.5m, categoryText: null, merchantText: null, CancellationToken.None);

        await handlers.HandleExpenseConfirmCallbackAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user,
            System.Globalization.CultureInfo.InvariantCulture, pending.Id, "d", CancellationToken.None);

        var recorded = Assert.Single(await expenses.GetRecentAsync(spaceId, userId, 10, CancellationToken.None));
        Assert.Equal(1.5m, recorded.Amount);
    }

    [Fact]
    public async Task HandleExpensesQueryAsync_ReturnsMonthlyTotal()
    {
        var handlers = CreateHandlers();
        await handlers.HandleExpenseAddAsync(
            expenses, budgets, notifications, undo, onboarding, address, spaceId, user,
            System.Globalization.CultureInfo.InvariantCulture, "10", categoryText: null, merchantText: null, CancellationToken.None);

        var result = await handlers.HandleExpensesQueryAsync(expenses, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, CancellationToken.None);

        var monthName = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(DateTime.UtcNow.Month);
        Assert.Equal($"In {monthName} you spent {MoneyFormatter.Format(10m, "EUR", "")}", result);
    }

    [Fact]
    public async Task HandleExpensesQueryByCategoryAsync_ReturnsNotFound_WhenCategoryIsUnknown()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleExpensesQueryByCategoryAsync(
            expenses, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "not-a-real-category", CancellationToken.None);

        Assert.Equal("I don't know a category called \"not-a-real-category\"", result);
    }

    [Fact]
    public async Task HandleExpenseCategorizeCallbackAsync_SetsCategory_AndLearnsMerchantMapping()
    {
        var handlers = CreateHandlers();
        var expense = await expenses.RecordAsync(spaceId, userId, 25m, categoryId: null, "Conad", DateOnly.FromDateTime(DateTime.UtcNow), note: null, CancellationToken.None);
        var categories = await expenses.GetCategoriesAsync(spaceId, CancellationToken.None);
        var groceriesIndex = categories.ToList().FindIndex(c => c.Id == SystemCategoryIds.Groceries);

        await handlers.HandleExpenseCategorizeCallbackAsync(expenses, address, spaceId, expense.Id, groceriesIndex, CancellationToken.None);

        var updated = Assert.Single(await expenses.GetRecentAsync(spaceId, userId, 10, CancellationToken.None));
        Assert.Equal(SystemCategoryIds.Groceries, updated.CategoryId);
        var learned = await expenses.FindMerchantCategoryAsync(spaceId, "Conad", CancellationToken.None);
        Assert.Equal(SystemCategoryIds.Groceries, learned?.Id);
        var sent = Assert.Single(channel.SentTexts);
        Assert.Equal("Got it — Conad will be categorized as Groceries from now on", sent.Text);
    }

    [Fact]
    public async Task HandleWarrantyReminderCallbackAsync_Dismissed_SendsDismissalText_AndCreatesNoReminder()
    {
        var handlers = CreateHandlers();
        var expense = await expenses.RecordAsync(spaceId, userId, 900m, categoryId: null, merchant: null, DateOnly.FromDateTime(DateTime.UtcNow), note: null, CancellationToken.None);
        var line = Assert.Single(await expenses.AddLinesAsync(expense.Id, [("Washing machine", 900m)], CancellationToken.None));

        await handlers.HandleWarrantyReminderCallbackAsync(
            expenses, reminders, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, line.Id, "no", CancellationToken.None);

        Assert.Equal("No problem — you can always set your own reminder later.", Assert.Single(channel.SentTexts).Text);
        Assert.Empty(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleWarrantyReminderCallbackAsync_Yes_CreatesAReminder()
    {
        var handlers = CreateHandlers();
        var expense = await expenses.RecordAsync(spaceId, userId, 900m, categoryId: null, merchant: null, DateOnly.FromDateTime(DateTime.UtcNow), note: null, CancellationToken.None);
        var line = Assert.Single(await expenses.AddLinesAsync(expense.Id, [("Washing machine", 900m)], CancellationToken.None));

        await handlers.HandleWarrantyReminderCallbackAsync(
            expenses, reminders, address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, line.Id, "yes", CancellationToken.None);

        var pending = Assert.Single(await reminders.GetPendingAsync(spaceId, userId, CancellationToken.None));
        Assert.Contains("Washing machine", pending.Text);
    }

    [Fact]
    public async Task HandleBudgetCommandAsync_SetsOverallBudget()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleBudgetCommandAsync(
            expenses, budgets, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, " 500", CancellationToken.None);

        Assert.Equal($"Overall monthly budget set to {MoneyFormatter.Format(500m, "EUR", "")}", result);
        var active = Assert.Single(await budgets.GetActiveAsync(spaceId, userId, CancellationToken.None));
        Assert.Null(active.CategoryId);
    }

    [Fact]
    public async Task HandleBudgetCommandAsync_ListsEmpty_WhenNoBudgetsAreSet()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleBudgetCommandAsync(
            expenses, budgets, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "", CancellationToken.None);

        Assert.Equal("No budgets set", result);
    }

    [Fact]
    public async Task HandleRecurringCommandAsync_CreatesAnAutoRegisterRecurringExpense()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRecurringCommandAsync(
            recurringExpenses, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, " monthly 50 rent", CancellationToken.None);

        Assert.Equal($"Recurring expense set: rent — {MoneyFormatter.Format(50m, "EUR", "")}, monthly", result);
        var active = Assert.Single(await recurringExpenses.GetActiveAsync(spaceId, userId, CancellationToken.None));
        Assert.True(active.AutoRegister);
    }

    [Fact]
    public async Task HandleRecurringCommandAsync_ListsEmpty_WhenNoneAreActive()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleRecurringCommandAsync(
            recurringExpenses, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, "", CancellationToken.None);

        Assert.Equal("No recurring expenses", result);
    }

    [Fact]
    public async Task HandleDigestCommandAsync_BuildsAndFormatsADigest_ForAnEmptySpace()
    {
        var handlers = CreateHandlers();

        var result = await handlers.HandleDigestCommandAsync(digest, expenses, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task HandleReceiptAsync_ReturnsNotConfigured_WhenNoVisionClientIsRegistered()
    {
        var handlers = CreateHandlers();
        var usage = new UsageService(Db);
        await using var scope = new ServiceCollection().BuildServiceProvider().CreateAsyncScope();
        var media = new InboundMedia("photo", "file-1", "receipt.jpg", "image/jpeg");

        var result = await handlers.HandleReceiptAsync(
            scope, shopping, expenses, budgets, notifications, undo, onboarding, usage,
            address, spaceId, user, System.Globalization.CultureInfo.InvariantCulture, media, CancellationToken.None);

        Assert.Equal("Reading receipts from photos isn't set up yet — you can still record the expense as text.", result);
    }

    private sealed class FakeChannelIdentityRepository : IChannelIdentityRepository
    {
        public Task<User?> ResolveUserAsync(string channelName, string externalUserId, CancellationToken ct) =>
            Task.FromResult<User?>(null);

        public Task<IReadOnlyList<ChannelIdentity>> GetForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChannelIdentity>>([]);
    }

    private sealed class FakeChannelRegistry : IChannelRegistry
    {
        public IChannel? TryGet(string channelName) => null;
    }
}
