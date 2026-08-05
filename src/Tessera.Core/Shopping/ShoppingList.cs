namespace Tessera.Core.Shopping;

public class ShoppingList
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string Name { get; set; } = "Spesa";
    public bool IsArchived { get; set; }

    public ICollection<ShoppingItem> Items { get; set; } = [];
}
