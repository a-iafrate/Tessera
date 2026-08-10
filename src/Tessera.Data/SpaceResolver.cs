using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// The precedence chain for "which space?" in a private chat (docs/02-modello-dati.md) — a
// group chat never reaches this, since GroupChatId already resolves the space directly.
// Resolved per resource, not per user: a single Write-access space for ShoppingList doesn't
// mean the same holds for Expenses.
public sealed class SpaceResolver(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<SpaceResolution> ResolveAsync(
        Guid userId, ResourceKind resource, AccessLevel required, string? messageText, CancellationToken ct)
    {
        var accessibleSpaceIds = await GetAccessibleSpaceIdsAsync(userId, resource, required, ct);

        // Explicit space in the message ("aggiungi latte in Casa") is checked against every
        // space the user belongs to, not just the ones with enough permission — naming a real
        // space you're just not allowed to use here is a different situation (and a different
        // reply) from not naming one at all (docs/10-conversazione.md).
        if (messageText is not null)
        {
            var allMemberSpaceIds = await db.Memberships.Where(m => m.UserId == userId).Select(m => m.SpaceId).ToListAsync(ct);
            var explicitMatch = await TryMatchExplicitSpaceAsync(allMemberSpaceIds, messageText, ct);
            if (explicitMatch is { } found)
            {
                if (accessibleSpaceIds.Contains(found.SpaceId))
                {
                    await SetActiveSpaceAsync(userId, found.SpaceId, ct);
                    return new SpaceResolution(found.SpaceId, found.RemainingText, []);
                }

                // Named a real space, but it doesn't have the permission this needs — resolve
                // a fallback from the accessible ones (steps 2-4 below, using the *stripped*
                // text) instead of silently acting in whichever space came out on top.
                var fallback = await ResolveAmongAccessibleAsync(userId, accessibleSpaceIds, found.RemainingText, ct);
                return fallback with { PermissionDeniedSpaceId = found.SpaceId };
            }
        }

        return await ResolveAmongAccessibleAsync(userId, accessibleSpaceIds, messageText, ct);
    }

    private async Task<SpaceResolution> ResolveAmongAccessibleAsync(
        Guid userId, IReadOnlyList<Guid> accessibleSpaceIds, string? messageText, CancellationToken ct)
    {
        if (accessibleSpaceIds.Count == 0)
        {
            return new SpaceResolution(null, messageText, []);
        }

        // 2. ConversationState.ActiveSpaceId within TTL — already disambiguated recently.
        var state = await db.ConversationStates
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ExpiresAt > DateTimeOffset.UtcNow, ct);
        if (state?.ActiveSpaceId is { } activeSpaceId && accessibleSpaceIds.Contains(activeSpaceId))
        {
            return new SpaceResolution(activeSpaceId, messageText, []);
        }

        // 3. User.DefaultSpaceId, set from the console.
        var user = await db.DomainUsers.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        if (user.DefaultSpaceId is { } defaultSpaceId && accessibleSpaceIds.Contains(defaultSpaceId))
        {
            return new SpaceResolution(defaultSpaceId, messageText, []);
        }

        // 4. Exactly one space has the required permission for this resource — no real
        // ambiguity even if the user belongs to several spaces overall.
        if (accessibleSpaceIds.Count == 1)
        {
            return new SpaceResolution(accessibleSpaceIds[0], messageText, []);
        }

        // 5. Genuinely ambiguous — the caller asks and remembers the answer.
        return new SpaceResolution(null, messageText, accessibleSpaceIds);
    }

    // Sets the answer from step 5, or from an explicit space name in step 1 — both count as
    // "just disambiguated" for the TTL window.
    public async Task SetActiveSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        var state = await db.ConversationStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is null)
        {
            state = new Core.Conversations.ConversationState { Id = Guid.NewGuid(), UserId = userId };
            db.ConversationStates.Add(state);
        }

        state.ActiveSpaceId = spaceId;
        state.PendingIntent = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<Guid>> GetAccessibleSpaceIdsAsync(
        Guid userId, ResourceKind resource, AccessLevel required, CancellationToken ct)
    {
        var memberSpaceIds = await db.Memberships
            .Where(m => m.UserId == userId)
            .Select(m => m.SpaceId)
            .ToListAsync(ct);

        var accessible = new List<Guid>();
        foreach (var spaceId in memberSpaceIds)
        {
            if (await accessPolicy.CanAsync(userId, spaceId, resource, required, ct))
            {
                accessible.Add(spaceId);
            }
        }

        return accessible;
    }

    private async Task<(Guid SpaceId, string RemainingText)?> TryMatchExplicitSpaceAsync(
        IReadOnlyList<Guid> candidateSpaceIds, string messageText, CancellationToken ct)
    {
        var spaces = await db.Spaces
            .Where(s => candidateSpaceIds.Contains(s.Id))
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var space in spaces)
        {
            var suffix = $" in {space.Name}";
            if (messageText.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (space.Id, messageText[..^suffix.Length].TrimEnd());
            }
        }

        return null;
    }
}
