namespace Tessera.Core.Expenses;

// One merchant's most recently observed price for a product (docs/13-piano-miglioramenti.md,
// E6 — "dove conviene comprare X"). A distinct question shape from PriceHistoryResult: that one
// compares the same product over time at a single point of reference; this compares different
// merchants against each other at (roughly) the same point in time.
public sealed record MerchantPriceObservation(string Merchant, decimal Price, DateOnly Date);
