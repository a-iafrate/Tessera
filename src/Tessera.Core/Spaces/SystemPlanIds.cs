namespace Tessera.Core.Spaces;

// Fixed ids for the seeded subscription plans (docs/02-modello-dati.md, docs/04-costi.md)
// so the default plan can be assigned at Space creation without a lookup.
//
// Two plans, not four (docs/13-piano-miglioramenti.md, D1) — Basic/Plus/Family collapsed into
// the single Plus id below (reusing the old Basic guid, since it's the one most likely to
// already have a provisioned PayPal plan id in a real environment); the old Plus/Family ids are
// retired by the AddValueBasedPlanFields migration, which also moves any Space still pointing
// at them onto this one before deleting the rows.
public static class SystemPlanIds
{
    public static readonly Guid Free = new("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Plus = new("10000000-0000-0000-0000-000000000002");
}
