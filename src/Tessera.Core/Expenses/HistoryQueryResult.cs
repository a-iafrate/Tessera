namespace Tessera.Core.Expenses;

public sealed record HistoryQueryResult(decimal? Amount, string? Currency, int Count, DateOnly? MostRecentDate);
