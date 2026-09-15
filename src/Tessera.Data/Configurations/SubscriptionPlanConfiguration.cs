using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.MonthlyPrice).HasPrecision(18, 2);

        // Two plans, not four (docs/13-piano-miglioramenti.md, D1) — value axes, not the old
        // calls/bots-shaped ones. MonthlyPrice on Plus is a placeholder like before: the actual
        // figure is a business decision tracked separately in the plan doc, not part of this
        // schema change.
        builder.HasData(
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Free, Name = "Free",
                MaxLinkedBots = 999, MaxCallsPerDay = 20, MonthlyPrice = 0m,
                MaxReceiptsPerMonth = 3, MaxLinkedCalendars = 1, HistoryMonths = 3,
                AllowsExport = false, MaxSpacesOwned = 1,
            },
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Plus, Name = "Plus",
                MaxLinkedBots = 999, MaxCallsPerDay = 1000, MonthlyPrice = 5m,
                MaxReceiptsPerMonth = 999, MaxLinkedCalendars = 999, HistoryMonths = 0,
                AllowsExport = true, MaxSpacesOwned = 999,
            });
    }
}
