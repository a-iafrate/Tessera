namespace Tessera.Core.Shopping;

public class ShoppingItem
{
    public Guid Id { get; set; }
    public Guid ShoppingListId { get; set; }
    public string RawText { get; set; } = null!;
    public string NormalizedName { get; set; } = null!;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public bool IsChecked { get; set; }
    public Guid AddedByUserId { get; set; }
    public Guid? CheckedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}
