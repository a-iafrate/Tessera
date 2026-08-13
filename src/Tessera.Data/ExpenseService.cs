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
        Guid spaceId, Guid userId, decimal amount, Guid? categoryId, string? merchant, DateOnly date,
        string? note, CancellationToken ct)
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
            Note = note,
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

    // The console list view (no equivalent exists on the bot side, which only ever shows
    // aggregates) — most recent first, capped by the caller.
    public async Task<IReadOnlyList<Expense>> GetRecentAsync(Guid spaceId, Guid userId, int take, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        return await db.Expenses
            .Where(x => x.SpaceId == spaceId)
            .OrderByDescending(x => x.Date)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);
    }

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

    // Historical search (docs/10-conversazione.md): "the variety of phrasings is too high for
    // pattern matching", so this exists to be composed from L3-extracted parameters — never
    // called with raw user text. Always returns one aggregate, never the matching rows.
    public async Task<HistoryQueryResult> QueryHistoryAsync(
        Guid spaceId, Guid userId, string? searchText, Guid? categoryId, DateOnly? dateFrom, DateOnly? dateTo,
        HistoryAggregation aggregation, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        var query = db.Expenses.Where(x => x.SpaceId == spaceId);
        if (searchText is { Length: > 0 })
        {
            var target = searchText.Trim().ToLower();
            query = query.Where(x =>
                (x.Merchant != null && x.Merchant.ToLower().Contains(target)) ||
                (x.Note != null && x.Note.ToLower().Contains(target)));
        }

        if (categoryId is not null)
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        if (dateFrom is not null)
        {
            query = query.Where(x => x.Date >= dateFrom.Value);
        }

        if (dateTo is not null)
        {
            query = query.Where(x => x.Date <= dateTo.Value);
        }

        switch (aggregation)
        {
            case HistoryAggregation.Total:
            {
                var total = await query.SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
                return new HistoryQueryResult(total, space.Currency, 0, null);
            }

            case HistoryAggregation.Average:
            {
                var amounts = query.Select(x => x.Amount);
                var count = await amounts.CountAsync(ct);
                decimal? average = count == 0 ? null : await amounts.AverageAsync(ct);
                return new HistoryQueryResult(average, space.Currency, count, null);
            }

            case HistoryAggregation.Count:
            {
                var count = await query.CountAsync(ct);
                return new HistoryQueryResult(null, null, count, null);
            }

            case HistoryAggregation.MostRecentDate:
            {
                var mostRecent = await query
                    .OrderByDescending(x => x.Date)
                    .Select(x => (DateOnly?)x.Date)
                    .FirstOrDefaultAsync(ct);
                return new HistoryQueryResult(null, null, 0, mostRecent);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(aggregation));
        }
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
