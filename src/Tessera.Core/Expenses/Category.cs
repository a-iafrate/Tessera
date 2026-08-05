namespace Tessera.Core.Expenses;

public class Category
{
    public Guid Id { get; set; }

    // Null means a system-default category, shared across all spaces.
    public Guid? SpaceId { get; set; }

    // Set for system categories: a resource key ("Category.Groceries") to localize.
    public string? ResourceKey { get; set; }

    // Set for user-created categories: free text, content — never localized.
    public string? Name { get; set; }
}
