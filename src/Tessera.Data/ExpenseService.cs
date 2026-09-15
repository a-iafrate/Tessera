using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tessera.Core.Abstractions;
using Tessera.Core.Expenses;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;
using Tessera.Data.Caching;

namespace Tessera.Data;

public sealed class ExpenseService(TesseraDbContext db, IAccessPolicy accessPolicy, IMemoryCache cache)
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

    // Persists the per-product lines extracted from a receipt (docs/06-roadmap.md "Storico
    // prezzi") — a best-effort addendum to RecordAsync, not access-checked on its own since
    // it only ever runs immediately after RecordAsync succeeded for the same expense. Returns
    // the created rows so the caller can check them against the warranty-reminder threshold
    // (docs/13-piano-miglioramenti.md, E2) without a second round trip.
    public async Task<IReadOnlyList<ExpenseLine>> AddLinesAsync(Guid expenseId, IEnumerable<(string Name, decimal Price)> items, CancellationToken ct)
    {
        var lines = items.Select(item => new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseId = expenseId,
            RawText = item.Name,
            NormalizedName = ProductNameNormalizer.Normalize(item.Name),
            Price = item.Price,
        }).ToList();

        db.ExpenseLines.AddRange(lines);
        await db.SaveChangesAsync(ct);
        return lines;
    }

    // Scoped by spaceId, not access-checked here — same convention as SetCategoryAsync: the
    // caller (MessageProcessor's callback dispatch) already gated on ResourceForCallback before
    // reaching this, and the spaceId filter is what turns a stale/foreign line id into a clean
    // null instead of leaking another space's data.
    public async Task<(ExpenseLine Line, Expense Expense)?> GetLineWithExpenseAsync(Guid spaceId, Guid lineId, CancellationToken ct)
    {
        var result = await db.ExpenseLines
            .Where(l => l.Id == lineId)
            .Join(db.Expenses, l => l.ExpenseId, e => e.Id, (l, e) => new { Line = l, Expense = e })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        if (result is null || result.Expense.SpaceId != spaceId)
        {
            return null;
        }

        return (result.Line, result.Expense);
    }

    // "Does coffee cost more than it used to" (docs/06-roadmap.md "Storico prezzi") — compares
    // the latest observed price for a product against a comparison point: the closest
    // observation on or before compareDate, or the earliest one on file if compareDate is
    // omitted. Built entirely from ExpenseLine rows recorded via receipt scanning.
    public async Task<PriceHistoryResult> QueryPriceHistoryAsync(
        Guid spaceId, Guid userId, string productText, DateOnly? compareDate, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);
        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);

        var target = ProductNameNormalizer.Normalize(productText);
        var observations = await db.ExpenseLines
            .Where(l => l.NormalizedName.Contains(target))
            .Join(db.Expenses, l => l.ExpenseId, e => e.Id, (l, e) => new { e.SpaceId, e.Date, e.Merchant, l.Price })
            .Where(x => x.SpaceId == spaceId)
            .OrderBy(x => x.Date)
            .AsNoTracking()
            .ToListAsync(ct);

        if (observations.Count == 0)
        {
            return new PriceHistoryResult(space.Currency, null, null, null, null, null, null, 0);
        }

        var latest = observations[^1];
        var comparison = compareDate is null
            ? observations[0]
            : observations.Where(o => o.Date <= compareDate.Value).OrderByDescending(o => o.Date).FirstOrDefault()
              ?? observations[0];

        return new PriceHistoryResult(
            space.Currency,
            latest.Price, latest.Date, latest.Merchant,
            comparison.Price, comparison.Date, comparison.Merchant,
            observations.Count);
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
    // "Quasi statiche" (docs/05-ottimizzazioni.md, 1h TTL) — today categories are only the
    // seeded system rows, so a per-space cache entry never actually needs invalidating; the
    // per-space key (rather than one shared entry) is kept anyway so a future per-space custom
    // category doesn't silently ship without cache correctness.
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(Guid spaceId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(CacheKeys.Categories(spaceId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl.Categories;
            return (IReadOnlyList<Category>)await db.Categories
                .AsNoTracking()
                .Where(x => x.SpaceId == null || x.SpaceId == spaceId)
                .OrderBy(x => x.Id)
                .ToListAsync(ct);
        }))!;

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

    // For the CSV export (docs/13-piano-miglioramenti.md, E3) — unlike GetRecentAsync (capped,
    // unfiltered, newest-first for the on-screen list) or QueryHistoryAsync (filtered but only
    // ever returns one aggregate, never rows, since it's built for L3 tool dispatch), this
    // returns every matching row, oldest-first (how a spreadsheet/commercialista expects a
    // ledger read top to bottom).
    public async Task<IReadOnlyList<Expense>> GetForExportAsync(
        Guid spaceId, Guid userId, DateOnly? dateFrom, DateOnly? dateTo, Guid? categoryId, CancellationToken ct)
    {
        await EnsureAccessAsync(spaceId, userId, AccessLevel.Read, ct);

        var space = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);
        if (!plan.AllowsExport)
        {
            throw new UnauthorizedAccessException($"Space {spaceId}'s plan does not include CSV export.");
        }

        var query = db.Expenses.Where(x => x.SpaceId == spaceId);
        if (dateFrom is { } from)
        {
            query = query.Where(x => x.Date >= from);
        }

        if (dateTo is { } to)
        {
            query = query.Where(x => x.Date <= to);
        }

        if (categoryId is { } category)
        {
            query = query.Where(x => x.CategoryId == category);
        }

        return await query.OrderBy(x => x.Date).AsNoTracking().ToListAsync(ct);
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
        var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(x => x.Id == space.PlanId, ct);

        // Narrows the effective range rather than rejecting the query outright
        // (docs/13-piano-miglioramenti.md, D1) — the L3 tool this feeds still answers
        // *something*, just for however much history the plan allows, instead of an error the
        // conversation has no good way to explain mid-flow. HistoryMonths <= 0 means unlimited.
        if (plan.HistoryMonths > 0)
        {
            var earliestAllowed = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-plan.HistoryMonths));
            dateFrom = dateFrom is null || dateFrom < earliestAllowed ? earliestAllowed : dateFrom;
        }

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
