namespace Tessera.Core.Expenses;

// One purchased product from a scanned receipt (docs/06-roadmap.md "Storico prezzi") —
// distinct from Expense itself, which stays the single total that gets budgeted and
// aggregated. Price is the amount printed on that receipt line, not a computed unit price:
// a multi-unit line ("3x latte 2,40") is stored as one line at 2,40, which is the same
// deliberate simplification as ShoppingItem's lowercase-and-trim normalization.
public class ExpenseLine
{
    public Guid Id { get; set; }
    public Guid ExpenseId { get; set; }
    public string RawText { get; set; } = null!;
    public string NormalizedName { get; set; } = null!;
    public decimal Price { get; set; }
}
