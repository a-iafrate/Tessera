namespace Tessera.Core.Expenses;

// What ExpenseService.QueryHistoryAsync computes — always a single aggregate, never a row
// dump (docs/10-conversazione.md: "il risultato è un'aggregazione, non un dump di righe").
public enum HistoryAggregation
{
    Total,
    Average,
    Count,
    MostRecentDate,
}
