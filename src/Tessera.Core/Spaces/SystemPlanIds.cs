namespace Tessera.Core.Spaces;

// Fixed ids for the seeded subscription plans (docs/02-modello-dati.md, docs/04-costi.md)
// so the default plan can be assigned at Space creation without a lookup.
public static class SystemPlanIds
{
    public static readonly Guid Free = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Basic = new("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Plus = new("10000000-0000-0000-0000-000000000003");
    public static readonly Guid Family = new("10000000-0000-0000-0000-000000000004");
}
