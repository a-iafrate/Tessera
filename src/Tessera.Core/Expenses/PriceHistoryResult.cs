namespace Tessera.Core.Expenses;

// What ExpenseService.QueryPriceHistoryAsync computes — the latest observed price for a
// product against a comparison point (an explicit date, or the earliest one on file), so
// the reply can say "coffee is up 15% since March" (docs/06-roadmap.md "Storico prezzi").
// All fields null/zero means no matching ExpenseLine exists yet.
public sealed record PriceHistoryResult(
    string Currency,
    decimal? LatestPrice,
    DateOnly? LatestDate,
    string? LatestMerchant,
    decimal? ComparisonPrice,
    DateOnly? ComparisonDate,
    string? ComparisonMerchant,
    int ObservationCount);
