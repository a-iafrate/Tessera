namespace Tessera.Core.Shopping;

public class ShoppingList
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }

    // Blank means "the space's original/default list" — rendered via a localized fallback,
    // never a hardcoded name (docs' hard rule: no user-facing text outside IStringLocalizer).
    // Only a list the user explicitly named (docs/10-conversazione.md, "liste generiche")
    // carries free text here.
    public string Name { get; set; } = "";

    public bool IsArchived { get; set; }

    public ICollection<ShoppingItem> Items { get; set; } = [];
}
