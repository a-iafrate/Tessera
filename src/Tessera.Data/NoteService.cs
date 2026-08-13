using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Notes;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class NoteService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<Note> CreateAsync(Guid spaceId, Guid userId, string? title, string body, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Title = title,
            Body = body,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task<IReadOnlyList<Note>> GetNotesAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        return await db.Notes
            .Where(x => x.SpaceId == spaceId)
            .OrderByDescending(x => x.UpdatedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    // Fuzzy match on title or body — the same "find the one you mean from free text" pattern
    // as ShoppingListService.RemoveItemAsync, used for natural-language deletion ("delete the
    // note about the wifi password").
    public async Task<Note?> FindNoteAsync(Guid spaceId, Guid userId, string searchText, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var target = searchText.Trim().ToLower();
        return await db.Notes
            .Where(x => x.SpaceId == spaceId
                && ((x.Title != null && x.Title.ToLower().Contains(target)) || x.Body.ToLower().Contains(target)))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    // For attaching an uncaptioned photo/document to "whatever I was just working on" — the
    // same "most recent thing this user touched" idea as UndoService.LastOperation, but scoped
    // to notes since that's the only resource attachments exist for today.
    public async Task<Note?> GetMostRecentByUserAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);
        return await db.Notes
            .Where(x => x.SpaceId == spaceId && (x.CreatedByUserId == userId || x.LastEditedByUserId == userId))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Note?> UpdateAsync(Guid spaceId, Guid userId, Guid noteId, string? title, string body, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == noteId && x.SpaceId == spaceId, ct);
        if (note is null)
        {
            return null;
        }

        note.Title = title;
        note.Body = body;
        note.LastEditedByUserId = userId;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task<Note?> DeleteAsync(Guid spaceId, Guid userId, Guid noteId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        var note = await db.Notes.FirstOrDefaultAsync(x => x.Id == noteId && x.SpaceId == spaceId, ct);
        if (note is null)
        {
            return null;
        }

        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        return note;
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.Notes, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException($"User {userId} lacks {required} access to Notes in space {spaceId}.");
        }
    }
}
