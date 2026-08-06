using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Expenses;
using Tessera.Core.Spaces;

namespace Tessera.Data;

public sealed class ExpenseService(TesseraDbContext db, IAccessPolicy accessPolicy)
{
    public async Task<string> GetSpaceCurrencyAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        return space.Currency;
    }

    public async Task<Expense> RecordAsync(
        Guid spaceId, Guid userId, decimal amount, Guid? categoryId, string? merchant, DateOnly date, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Write, ct);

        // Copied at creation, not just derived from the space: if the space's currency
        // ever changes, past expenses must not silently change meaning (docs/02-modello-dati.md).
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Amount = amount,
            Currency = space.Currency,
            CategoryId = categoryId,
            Merchant = merchant,
            Date = date,
            CreatedByUserId = userId,
        };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return expense;
    }

    // Merchant learning (docs/02-modello-dati.md): per space, not global — "Esselunga"
    // can mean groceries for one family and something else for another.
    public async Task<Category?> FindMerchantCategoryAsync(Guid spaceId, string merchant, CancellationToken ct)
    {
        var normalized = Normalize(merchant);
        var mapping = await db.MerchantCategoryMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SpaceId == spaceId && x.MerchantNormalized == normalized, ct);
        if (mapping is null)
        {
            return null;
        }

        return await db.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mapping.CategoryId, ct);
    }

    // Asked once via inline keyboard, then never again for that merchant — this is where
    // the answer gets remembered.
    public async Task LearnMerchantCategoryAsync(Guid spaceId, string merchant, Guid categoryId, CancellationToken ct)
    {
        var normalized = Normalize(merchant);
        var mapping = await db.MerchantCategoryMappings
            .FirstOrDefaultAsync(x => x.SpaceId == spaceId && x.MerchantNormalized == normalized, ct);

        if (mapping is null)
        {
            db.MerchantCategoryMappings.Add(new MerchantCategoryMapping
            {
                SpaceId = spaceId,
                MerchantNormalized = normalized,
                CategoryId = categoryId,
                ConfirmationCount = 1,
            });
        }
        else
        {
            mapping.CategoryId = categoryId;
            mapping.ConfirmationCount++;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Expense?> SetCategoryAsync(Guid spaceId, Guid expenseId, Guid categoryId, CancellationToken ct)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(x => x.Id == expenseId && x.SpaceId == spaceId, ct);
        if (expense is null)
        {
            return null;
        }

        expense.CategoryId = categoryId;
        await db.SaveChangesAsync(ct);
        return expense;
    }

    private static string Normalize(string merchant) => merchant.Trim().ToLowerInvariant();

    // Bridges the ambiguous-amount confirmation across the inline-keyboard round trip —
    // callback_data can't carry free-text category/merchant, so it lives here instead,
    // referenced by a short id (docs/07-compliance.md's LinkToken TTL pattern).
    public async Task<PendingExpenseConfirmation> CreatePendingConfirmationAsync(
        Guid spaceId, Guid userId, decimal candidateAsGrouped, decimal candidateAsDecimal,
        string? categoryText, string? merchantText, CancellationToken ct)
    {
        var pending = new PendingExpenseConfirmation
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            UserId = userId,
            CandidateAsGrouped = candidateAsGrouped,
            CandidateAsDecimal = candidateAsDecimal,
            CategoryText = categoryText,
            MerchantText = merchantText,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        db.PendingExpenseConfirmations.Add(pending);
        await db.SaveChangesAsync(ct);
        return pending;
    }

    // Null covers both failure cases (not found, expired) — the caller just re-asks the
    // amount rather than trying to distinguish why.
    public async Task<PendingExpenseConfirmation?> ConsumePendingConfirmationAsync(
        Guid spaceId, Guid pendingId, CancellationToken ct)
    {
        var pending = await db.PendingExpenseConfirmations
            .FirstOrDefaultAsync(x => x.Id == pendingId && x.SpaceId == spaceId, ct);
        if (pending is null || pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        db.PendingExpenseConfirmations.Remove(pending);
        await db.SaveChangesAsync(ct);
        return pending;
    }

    // Deterministic order: callers reference a category by its position in this list
    // (Telegram's callback_data is capped at 64 bytes — too little for two GUIDs — so the
    // inline keyboard encodes an index here instead of the category id).
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(Guid spaceId, CancellationToken ct) =>
        await db.Categories
            .AsNoTracking()
            .Where(x => x.SpaceId == null || x.SpaceId == spaceId)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

    public async Task<(decimal Amount, string Currency)> GetMonthlyTotalAsync(
        Guid spaceId, Guid userId, int year, int month, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        // Aggregation in SQL, not ToList().Sum() — docs/05-ottimizzazioni.md.
        var total = await db.Expenses
            .Where(x => x.SpaceId == spaceId && x.Date.Year == year && x.Date.Month == month)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

        return (total, space.Currency);
    }

    public async Task<(decimal Amount, string Currency)> GetCategoryTotalAsync(
        Guid spaceId, Guid userId, Guid categoryId, int year, int month, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        var total = await db.Expenses
            .Where(x => x.SpaceId == spaceId && x.CategoryId == categoryId
                && x.Date.Year == year && x.Date.Month == month)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

        return (total, space.Currency);
    }

    private async Task EnsureAccessAsync(Guid spaceId, Guid userId, AccessLevel required, CancellationToken ct)
    {
        var allowed = await accessPolicy.CanAsync(userId, spaceId, ResourceKind.Expenses, required, ct);
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"User {userId} lacks {required} access to Expenses in space {spaceId}.");
        }
    }
}
