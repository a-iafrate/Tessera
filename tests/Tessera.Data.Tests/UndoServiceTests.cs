using Microsoft.EntityFrameworkCore;
using Tessera.Core.Expenses;
using Tessera.Core.Shopping;

namespace Tessera.Data.Tests;

// LastOperation is a single slot per user, not a stack (docs/10-conversazione.md) — recording a
// second operation replaces the first, and only the latest is ever undoable. Two separate TTLs
// apply to the same row: a 10-minute undo window, and a tighter 2-minute "correction" window
// used only to offer a same-breath fix right after a shopping.add (docs/13-piano-miglioramenti.md, F4).
public class UndoServiceTests : IDisposable
{
    private readonly TestDatabase testDb = new();
    private TesseraDbContext Db => testDb.Db;
    private readonly UndoService undo;

    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid spaceId = Guid.NewGuid();
    private readonly Guid listId = Guid.NewGuid();

    public UndoServiceTests()
    {
        undo = new UndoService(Db);
    }

    public void Dispose() => testDb.Dispose();

    private async Task<ShoppingItem> AddShoppingItemAsync(string text = "latte")
    {
        Db.ShoppingLists.Add(new ShoppingList { Id = listId, SpaceId = spaceId });
        var item = new ShoppingItem
        {
            Id = Guid.NewGuid(), ShoppingListId = listId, RawText = text, NormalizedName = text,
            AddedByUserId = userId, AddedAt = DateTimeOffset.UtcNow,
        };
        Db.ShoppingItems.Add(item);
        await Db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task TryUndoLastAsync_ReturnsNothingPending_WhenNoOperationWasEverRecorded()
    {
        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.IsType<UndoNothingPending>(result);
    }

    [Fact]
    public async Task RecordShoppingAddAsync_ThenTryUndoLastAsync_RemovesTheItem()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);

        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        var succeeded = Assert.IsType<UndoSucceeded>(result);
        Assert.Equal("shopping.add", succeeded.OperationType);
        Assert.False(await Db.ShoppingItems.AnyAsync(x => x.Id == item.Id));
    }

    [Fact]
    public async Task TryUndoLastAsync_ReturnsNothingPending_WhenAlreadyUndoneOnce()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);
        await undo.TryUndoLastAsync(userId, CancellationToken.None);

        var second = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.IsType<UndoNothingPending>(second);
    }

    [Fact]
    public async Task TryUndoLastAsync_ReturnsNothingPending_OncePastTheTenMinuteWindow()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);
        var op = await Db.LastOperations.SingleAsync(x => x.UserId == userId);
        op.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Db.SaveChangesAsync();

        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.IsType<UndoNothingPending>(result);
    }

    [Fact]
    public async Task TryUndoLastAsync_ReturnsConflict_WhenTheShoppingItemWasCheckedByADifferentUser()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);

        item.IsChecked = true;
        item.CheckedByUserId = Guid.NewGuid();
        item.CheckedAt = DateTimeOffset.UtcNow;
        await Db.SaveChangesAsync();

        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.IsType<UndoConflict>(result);
        Assert.True(await Db.ShoppingItems.AnyAsync(x => x.Id == item.Id), "the item must not be deleted on conflict");
    }

    [Fact]
    public async Task RecordingASecondOperation_OverwritesTheFirst_OnlyTheLatestIsUndoable()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);

        Db.Expenses.Add(new Expense { Id = Guid.NewGuid(), SpaceId = spaceId, Amount = 10m, Date = DateOnly.FromDateTime(DateTime.UtcNow), CreatedByUserId = userId });
        await Db.SaveChangesAsync();
        var expenseId = await Db.Expenses.Where(x => x.SpaceId == spaceId).Select(x => x.Id).FirstAsync();
        await undo.RecordExpenseAsync(userId, spaceId, expenseId, CancellationToken.None);

        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        var succeeded = Assert.IsType<UndoSucceeded>(result);
        Assert.Equal("expense.record", succeeded.OperationType);
        // The shopping item from the first (overwritten) operation is untouched.
        Assert.True(await Db.ShoppingItems.AnyAsync(x => x.Id == item.Id));
    }

    [Fact]
    public async Task GetRecentCorrectableActionAsync_ReturnsNull_AfterTheTwoMinuteCorrectionWindow_EvenThoughStillUndoable()
    {
        var item = await AddShoppingItemAsync();
        await undo.RecordShoppingAddAsync(userId, spaceId, item.Id, CancellationToken.None);
        var op = await Db.LastOperations.SingleAsync(x => x.UserId == userId);
        op.PerformedAt = DateTimeOffset.UtcNow.AddMinutes(-3); // past the 2-min correction window
        op.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(7);    // still inside the 10-min undo window
        await Db.SaveChangesAsync();

        var correctable = await undo.GetRecentCorrectableActionAsync(userId, CancellationToken.None);
        var undoResult = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.Null(correctable);
        Assert.IsType<UndoSucceeded>(undoResult);
    }

    [Fact]
    public async Task RecordShoppingClearAsync_ThenUndo_RestoresEveryItem_WithNewIdsButTheSameContent()
    {
        Db.ShoppingLists.Add(new ShoppingList { Id = listId, SpaceId = spaceId });
        await Db.SaveChangesAsync();
        var cleared = new List<ShoppingItem>
        {
            new() { Id = Guid.NewGuid(), ShoppingListId = listId, RawText = "latte", NormalizedName = "latte", AddedByUserId = userId, AddedAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), ShoppingListId = listId, RawText = "pane", NormalizedName = "pane", AddedByUserId = userId, AddedAt = DateTimeOffset.UtcNow, IsChecked = true, CheckedByUserId = userId, CheckedAt = DateTimeOffset.UtcNow },
        };
        await undo.RecordShoppingClearAsync(userId, spaceId, cleared, CancellationToken.None);

        var result = await undo.TryUndoLastAsync(userId, CancellationToken.None);

        Assert.IsType<UndoSucceeded>(result);
        var restored = await Db.ShoppingItems.Where(x => x.ShoppingListId == listId).ToListAsync();
        Assert.Equal(2, restored.Count);
        Assert.Contains(restored, x => x.RawText == "latte" && !x.IsChecked);
        Assert.Contains(restored, x => x.RawText == "pane" && x.IsChecked);
        Assert.DoesNotContain(restored, x => cleared.Select(c => c.Id).Contains(x.Id));
    }
}
