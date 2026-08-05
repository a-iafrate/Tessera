using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// Every query filters by SpaceId — never by UserId — per the sharing model in
// docs/02-modello-dati.md. Matching an item by name is a rough stand-in for the inline
// keyboard (a separate, later checklist item): it works today because there is no way
// yet to pick an item by id from the chat.
public sealed class ShoppingListService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    private static readonly Regex LeadingArticle = new(
        @"^(il|lo|la|i|gli|le|un|uno|una|the|a|an)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<ShoppingItem> AddItemAsync(Guid spaceId, Guid userId, string rawText, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await GetOrCreateListAsync(spaceId, ct);

        var item = new ShoppingItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = list.Id,
            RawText = rawText,
            NormalizedName = Normalize(rawText),
            AddedByUserId = userId,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.ShoppingItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<ShoppingItem?> CheckItemAsync(Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await GetOrCreateListAsync(spaceId, ct);
        var target = Normalize(itemText);

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

    public async Task<ShoppingItem?> RemoveItemAsync(Guid spaceId, Guid userId, string itemText, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await GetOrCreateListAsync(spaceId, ct);
        var target = Normalize(itemText);

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

    public async Task<IReadOnlyList<ShoppingItem>> GetItemsAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var list = await GetOrCreateListAsync(spaceId, ct);

        return await db.ShoppingItems
            .Where(x => x.ShoppingListId == list.Id)
            .OrderBy(x => x.IsChecked)
            .ThenBy(x => x.AddedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task ClearAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        var list = await GetOrCreateListAsync(spaceId, ct);

        var items = await db.ShoppingItems.Where(x => x.ShoppingListId == list.Id).ToListAsync(ct);
        db.ShoppingItems.RemoveRange(items);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.ShoppingList, required, ct);
        if (!allowed)
        {
            // No permission-denied reply flow yet (docs/10-conversazione.md): unreachable
            // today since every user only has their own personal space, where they're
            // always the owner. Revisit once sharing/invites exist.
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to ShoppingList in space {spaceId}.");
        }
    }

    private async Task<ShoppingList> GetOrCreateListAsync(Guid spaceId, CancellationToken ct)
    {
        var list = await db.ShoppingLists.FirstOrDefaultAsync(x => x.SpaceId == spaceId && !x.IsArchived, ct);
        if (list is not null)
        {
            return list;
        }

        list = new ShoppingList { Id = Guid.NewGuid(), SpaceId = spaceId };
        db.ShoppingLists.Add(list);
        await db.SaveChangesAsync(ct);
        return list;
    }

    private static string Normalize(string rawText)
    {
        var text = LeadingArticle.Replace(rawText.Trim().ToLowerInvariant(), "");
        return text.Trim();
    }
}
