using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// Every query filters by SpaceId — never by UserId — per the sharing model in
// docs/02-modello-dati.md. CheckItemAsync/RemoveItemAsync match by (fuzzy) name for plain
// text commands; CheckItemByIdAsync is the exact-match path used by the inline keyboard.
//
// listName is optional everywhere (docs/10-conversazione.md, "liste generiche"): omitted, it
// resolves to the space's original/default list — the common case, and the only case that
// existed before named lists — so callers that never mention a list see no behavior change.
// Named, it resolves (or creates, on add) that specific list instead.
public sealed class ShoppingListService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<ShoppingItem> AddItemAsync(
        Guid spaceId, Guid userId, string rawText, string? listName, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await ResolveListAsync(spaceId, listName, createIfMissing: true, ct);

        var item = new ShoppingItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = list!.Id,
            RawText = rawText,
            NormalizedName = ProductNameNormalizer.Normalize(rawText),
            AddedByUserId = userId,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.ShoppingItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<ShoppingItem?> CheckItemAsync(
        Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await ResolveListAsync(spaceId, listName, createIfMissing: false, ct);
        if (list is null)
        {
            return null;
        }

        var target = ProductNameNormalizer.Normalize(itemText);
        var item = await db.ShoppingItems
            .Where(x => x.ShoppingListId == list.Id && !x.IsChecked && x.NormalizedName.Contains(target))
            .OrderBy(x => x.AddedAt)
            .FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return null;
        }

        item.IsChecked = true;
        item.CheckedByUserId = userId;
        item.CheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return item;
    }

    // Exact-match path for the inline keyboard — the item may sit on any of the space's
    // lists, not just the default one, so it's looked up directly rather than through
    // ResolveListAsync (docs/10-conversazione.md, "liste generiche").
    public async Task<ShoppingItem?> CheckItemByIdAsync(Guid spaceId, Guid userId, Guid itemId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var item = await FindItemInSpaceAsync(spaceId, itemId, ct);
        if (item is null || item.IsChecked)
        {
            return null;
        }

        item.IsChecked = true;
        item.CheckedByUserId = userId;
        item.CheckedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<ShoppingItem?> RemoveItemAsync(
        Guid spaceId, Guid userId, string itemText, string? listName, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await ResolveListAsync(spaceId, listName, createIfMissing: false, ct);
        if (list is null)
        {
            return null;
        }

        var target = ProductNameNormalizer.Normalize(itemText);
        var item = await db.ShoppingItems
            .Where(x => x.ShoppingListId == list.Id && x.NormalizedName.Contains(target))
            .OrderBy(x => x.AddedAt)
            .FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return null;
        }

        db.ShoppingItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    // Rewrites an item in place rather than creating a new one — "no, 2 litri" right after
    // adding "latte" is a correction, not a second item (docs/10-conversazione.md). Refuses
    // if it's already checked off: silently rewriting something another member already acted
    // on would be worse than the correction not applying. Looked up directly, like
    // CheckItemByIdAsync, since the item may be on any of the space's lists.
    public async Task<ShoppingItem?> CorrectItemAsync(
        Guid spaceId, Guid userId, Guid itemId, string correctedText, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var item = await FindItemInSpaceAsync(spaceId, itemId, ct);
        if (item is null || item.IsChecked)
        {
            return null;
        }

        item.RawText = correctedText;
        item.NormalizedName = ProductNameNormalizer.Normalize(correctedText);
        await db.SaveChangesAsync(ct);
        return item;
    }

    // Exact-match counterpart to RemoveItemAsync, same reasoning as CheckItemByIdAsync — the
    // console list renders specific rows with delete buttons, so it deletes by id rather than
    // re-running the fuzzy text match against whatever the user last typed.
    public async Task<ShoppingItem?> RemoveItemByIdAsync(Guid spaceId, Guid userId, Guid itemId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var item = await FindItemInSpaceAsync(spaceId, itemId, ct);
        if (item is null)
        {
            return null;
        }

        db.ShoppingItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<IReadOnlyList<ShoppingItem>> GetItemsAsync(
        Guid spaceId, Guid userId, string? listName, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var list = await ResolveListAsync(spaceId, listName, createIfMissing: false, ct);
        if (list is null)
        {
            return [];
        }

        return await db.ShoppingItems
            .Where(x => x.ShoppingListId == list.Id)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.AddedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // Used to refresh the exact list a checked/removed item belonged to (the inline-keyboard
    // edit-in-place path in MessageProcessor) — GetItemsAsync resolves by name, but callback
    // handlers only learn the item's ShoppingListId after CheckItemByIdAsync/RemoveItemByIdAsync
    // have already resolved it.
    public async Task<IReadOnlyList<ShoppingItem>> GetItemsByListIdAsync(
        Guid spaceId, Guid userId, Guid listId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        return await db.ShoppingItems
            .Where(x => x.ShoppingListId == listId)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.AddedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // Returns what was removed so the caller can offer an undo (docs/10-conversazione.md:
    // "è l'undo che serve di più" — without the removed rows, a clear is unrecoverable).
    public async Task<IReadOnlyList<ShoppingItem>> ClearAsync(
        Guid spaceId, Guid userId, string? listName, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await ResolveListAsync(spaceId, listName, createIfMissing: false, ct);
        if (list is null)
        {
            return [];
        }

        var items = await db.ShoppingItems.Where(x => x.ShoppingListId == list.Id).ToListAsync(ct);
        db.ShoppingItems.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return items;
    }

    // Every named list in the space, oldest first — the oldest is always the implicit
    // default (docs/10-conversazione.md: "la lista di default per spazio risolve la quasi
    // totalità dei casi").
    public async Task<IReadOnlyList<ShoppingList>> GetListsAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        return await db.ShoppingLists
            .Where(x => x.SpaceId == spaceId && !x.IsArchived)
            .OrderBy(x => x.Id)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.ShoppingList, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to ShoppingList in space {spaceId}.");
        }
    }

    private async Task<ShoppingList?> ResolveListAsync(
        Guid spaceId, string? listName, bool createIfMissing, CancellationToken ct)
    {
        if (listName is null)
        {
            var defaultList = await db.ShoppingLists
                .Where(x => x.SpaceId == spaceId && !x.IsArchived)
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (defaultList is not null)
            {
                return defaultList;
            }

            defaultList = new ShoppingList { Id = Guid.NewGuid(), SpaceId = spaceId };
            db.ShoppingLists.Add(defaultList);
            await db.SaveChangesAsync(ct);
            return defaultList;
        }

        var trimmedName = listName.Trim();
        var named = await db.ShoppingLists
            .FirstOrDefaultAsync(x => x.SpaceId == spaceId && !x.IsArchived && x.Name.ToLower() == trimmedName.ToLower(), ct);
        if (named is not null || !createIfMissing)
        {
            return named;
        }

        named = new ShoppingList { Id = Guid.NewGuid(), SpaceId = spaceId, Name = trimmedName };
        db.ShoppingLists.Add(named);
        await db.SaveChangesAsync(ct);
        return named;
    }

    private async Task<ShoppingItem?> FindItemInSpaceAsync(Guid spaceId, Guid itemId, CancellationToken ct) =>
        await db.ShoppingItems
            .Where(i => i.Id == itemId)
            .Join(db.ShoppingLists, i => i.ShoppingListId, l => l.Id, (i, l) => new { Item = i, l.SpaceId })
            .Where(x => x.SpaceId == spaceId)
            .Select(x => x.Item)
            .FirstOrDefaultAsync(ct);
}
