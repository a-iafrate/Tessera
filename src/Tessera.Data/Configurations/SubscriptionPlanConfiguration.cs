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
        builder.Property(x => x.AnnualPrice).HasPrecision(18, 2);

        // Two plans, not four (docs/13-piano-miglioramenti.md, D1) — value axes, not the old
        // calls/bots-shaped ones. MonthlyPrice/AnnualPrice on Plus are placeholders like before:
        // the actual figures are a business decision tracked separately in the plan doc, not
        // part of this schema change. AnnualPrice = 10x monthly ("two months free") is the
        // conventional SaaS annual discount (docs/13, D2), not a computed default — it's its own
        // stored value so it can be tuned independently.
        builder.HasData(
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Free, Name = "Free",
                MaxLinkedBots = 999, MaxCallsPerDay = 20, MonthlyPrice = 0m, AnnualPrice = 0m,
                MaxReceiptsPerMonth = 3, MaxLinkedCalendars = 1, HistoryMonths = 3,
                AllowsExport = false, MaxSpacesOwned = 1,
            },
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Plus, Name = "Plus",
                MaxLinkedBots = 999, MaxCallsPerDay = 1000, MonthlyPrice = 5m, AnnualPrice = 50m,
                MaxReceiptsPerMonth = 999, MaxLinkedCalendars = 999, HistoryMonths = 0,
                AllowsExport = true, MaxSpacesOwned = 999,
            });
    }
}
