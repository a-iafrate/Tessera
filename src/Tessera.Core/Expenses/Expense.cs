namespace Tessera.Core.Expenses;

public class Expense
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Merchant { get; set; }
    public Guid? CategoryId { get; set; }
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }
    public ICollection<ExpenseLine> Lines { get; set; } = [];
}
