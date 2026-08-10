using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Conversations;
using Tessera.Core.Shopping;

namespace Tessera.Data;

public abstract record UndoOutcome;

public sealed record UndoSucceeded(string OperationType) : UndoOutcome;

public sealed record UndoNothingPending : UndoOutcome;

// Someone else touched the same resource since the operation was recorded — refuse rather
// than silently overwrite what they did (docs/10-conversazione.md).
public sealed record UndoConflict : UndoOutcome;

internal sealed record ShoppingAddUndoPayload(Guid ItemId);

internal sealed record ShoppingCheckUndoPayload(Guid ItemId);

internal sealed record ExpenseRecordUndoPayload(Guid ExpenseId);

internal sealed record ReminderCreateUndoPayload(Guid ReminderId);

internal sealed record NoteCreateUndoPayload(Guid NoteId);

internal sealed record ClearedItem(
    string RawText, string NormalizedName, decimal? Quantity, string? Unit,
    Guid AddedByUserId, DateTimeOffset AddedAt, bool IsChecked, Guid? CheckedByUserId, DateTimeOffset? CheckedAt);

internal sealed record ShoppingClearUndoPayload(IReadOnlyList<ClearedItem> Items);

// The single reversible operation per user (docs/10-conversazione.md) — one row, not a
// stack: a multi-level undo in chat is confusing, and per-user rather than per-space so one
// member's "undo" never touches another member's edit.
public sealed record RecentCorrectableAction(Guid ItemId, string Description);

public sealed class UndoService(TesseraDbContext db)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    // Tighter than the undo TTL (docs/10-conversazione.md: "un messaggio breve e correttivo
    // entro pochi secondi da un'operazione") — a correction only makes sense as an immediate
    // follow-up, not something to offer minutes later when the user is on to something else.
    private static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(2);

    // Only shopping.add is correctable today (docs/10-conversazione.md's own example is the
    // shopping list) — refuses if someone already checked the item off in the meantime,
    // the same shared-resource guard undo itself uses.
    public async Task<RecentCorrectableAction?> GetRecentCorrectableActionAsync(Guid userId, CancellationToken ct)
    {
        var op = await db.LastOperations.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (op is null || op.IsUndone || op.OperationType != "shopping.add"
            || op.PerformedAt < DateTimeOffset.UtcNow - CorrectionWindow)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<ShoppingAddUndoPayload>(op.UndoPayloadJson)!;
        var item = await db.ShoppingItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == payload.ItemId, ct);
        if (item is null || item.IsChecked)
        {
            return null;
        }

        return new RecentCorrectableAction(item.Id, $"added \"{item.RawText}\" to the shopping list");
    }

    public Task RecordShoppingAddAsync(Guid userId, Guid spaceId, Guid itemId, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "shopping.add", new ShoppingAddUndoPayload(itemId), ct);

    public Task RecordShoppingCheckAsync(Guid userId, Guid spaceId, Guid itemId, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "shopping.check", new ShoppingCheckUndoPayload(itemId), ct);

    public Task RecordShoppingClearAsync(Guid userId, Guid spaceId, IReadOnlyList<ShoppingItem> items, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "shopping.clear", new ShoppingClearUndoPayload(items
            .Select(i => new ClearedItem(
                i.RawText, i.NormalizedName, i.Quantity, i.Unit, i.AddedByUserId, i.AddedAt,
                i.IsChecked, i.CheckedByUserId, i.CheckedAt))
            .ToList()), ct);

    public Task RecordExpenseAsync(Guid userId, Guid spaceId, Guid expenseId, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "expense.record", new ExpenseRecordUndoPayload(expenseId), ct);

    public Task RecordReminderAsync(Guid userId, Guid spaceId, Guid reminderId, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "reminder.create", new ReminderCreateUndoPayload(reminderId), ct);

    public Task RecordNoteAsync(Guid userId, Guid spaceId, Guid noteId, CancellationToken ct) =>
        SaveAsync(userId, spaceId, "note.create", new NoteCreateUndoPayload(noteId), ct);

    public async Task<UndoOutcome> TryUndoLastAsync(Guid userId, CancellationToken ct)
    {
        var op = await db.LastOperations.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (op is null || op.IsUndone || op.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return new UndoNothingPending();
        }

        var reverted = op.OperationType switch
        {
            "shopping.add" => await UndoShoppingAddAsync(op, ct),
            "shopping.check" => await UndoShoppingCheckAsync(op, userId, ct),
            "shopping.clear" => await UndoShoppingClearAsync(op, ct),
            "expense.record" => await UndoExpenseAsync(op, ct),
            "reminder.create" => await UndoReminderAsync(op, ct),
            "note.create" => await UndoNoteAsync(op, ct),
            _ => false,
        };

        if (!reverted)
        {
            return new UndoConflict();
        }

        op.IsUndone = true;
        await db.SaveChangesAsync(ct);
        return new UndoSucceeded(op.OperationType);
    }

    private async Task SaveAsync<T>(Guid userId, Guid spaceId, string operationType, T payload, CancellationToken ct)
    {
        var op = await db.LastOperations.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (op is null)
        {
            op = new LastOperation { UserId = userId };
            db.LastOperations.Add(op);
        }

        op.SpaceId = spaceId;
        op.OperationType = operationType;
        op.UndoPayloadJson = JsonSerializer.Serialize(payload);
        op.PerformedAt = DateTimeOffset.UtcNow;
        op.ExpiresAt = DateTimeOffset.UtcNow.Add(Ttl);
        op.IsUndone = false;
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> UndoShoppingAddAsync(LastOperation op, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ShoppingAddUndoPayload>(op.UndoPayloadJson)!;
        var item = await db.ShoppingItems.FirstOrDefaultAsync(x => x.Id == payload.ItemId, ct);
        if (item is null || item.IsChecked)
        {
            return false;
        }

        db.ShoppingItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UndoShoppingCheckAsync(LastOperation op, Guid userId, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ShoppingCheckUndoPayload>(op.UndoPayloadJson)!;
        var item = await db.ShoppingItems.FirstOrDefaultAsync(x => x.Id == payload.ItemId, ct);
        if (item is null || !item.IsChecked || item.CheckedByUserId != userId)
        {
            return false;
        }

        item.IsChecked = false;
        item.CheckedByUserId = null;
        item.CheckedAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UndoShoppingClearAsync(LastOperation op, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ShoppingClearUndoPayload>(op.UndoPayloadJson)!;
        var list = await db.ShoppingLists.FirstOrDefaultAsync(x => x.SpaceId == op.SpaceId && !x.IsArchived, ct);
        if (list is null)
        {
            return false;
        }

        foreach (var cleared in payload.Items)
        {
            db.ShoppingItems.Add(new ShoppingItem
            {
                Id = Guid.NewGuid(),
                ShoppingListId = list.Id,
                RawText = cleared.RawText,
                NormalizedName = cleared.NormalizedName,
                Quantity = cleared.Quantity,
                Unit = cleared.Unit,
                AddedByUserId = cleared.AddedByUserId,
                AddedAt = cleared.AddedAt,
                IsChecked = cleared.IsChecked,
                CheckedByUserId = cleared.CheckedByUserId,
                CheckedAt = cleared.CheckedAt,
            });
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UndoExpenseAsync(LastOperation op, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ExpenseRecordUndoPayload>(op.UndoPayloadJson)!;
        var expense = await db.Expenses.FirstOrDefaultAsync(x => x.Id == payload.ExpenseId, ct);
        if (expense is null)
        {
            return false;
        }

        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UndoReminderAsync(LastOperation op, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ReminderCreateUndoPayload>(op.UndoPayloadJson)!;
        var reminder = await db.Reminders.FirstOrDefaultAsync(x => x.Id == payload.ReminderId, ct);
        if (reminder is null || reminder.IsCompleted)
        {
            return false;
        }

        db.Reminders.Remove(reminder);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> UndoNoteAsync(LastOperation op, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<NoteCreateUndoPayload>(op.UndoPayloadJson)!;
        var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == payload.NoteId, ct);
        if (note is null)
        {
            return false;
        }

        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
