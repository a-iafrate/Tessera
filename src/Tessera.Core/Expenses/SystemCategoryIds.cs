namespace Tessera.Core.Expenses;

// Fixed ids for the seeded system categories (docs/02-modello-dati.md, docs/09-localizzazione.md)
// so they can be referenced without a lookup — e.g. a future "Other" fallback.
public static class SystemCategoryIds
{
    public static readonly Guid Groceries = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Transport = new("00000000-0000-0000-0000-000000000002");
    public static readonly Guid Utilities = new("00000000-0000-0000-0000-000000000003");
    public static readonly Guid Entertainment = new("00000000-0000-0000-0000-000000000004");
    public static readonly Guid Health = new("00000000-0000-0000-0000-000000000005");
    public static readonly Guid Other = new("00000000-0000-0000-0000-000000000006");
}
