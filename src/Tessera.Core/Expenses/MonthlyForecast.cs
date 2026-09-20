namespace Tessera.Core.Expenses;

// "Previsione di fine mese" (docs/13-piano-miglioramenti.md, E7) — current spend plus every
// active, auto-registering recurring expense not yet generated this month. A deterministic
// floor built from known future charges (rent, subscriptions), not a statistical extrapolation
// of day-to-day spending — recurring expenses are the one category of future spend this app
// actually knows about in advance.
public sealed record MonthlyForecast(decimal SpentSoFar, decimal PendingRecurring, decimal ProjectedTotal, string Currency);
