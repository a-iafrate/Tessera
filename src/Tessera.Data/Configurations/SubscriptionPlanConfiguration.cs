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

        // Placeholder figures (docs/04-costi.md) — adjust freely, the schema doesn't change.
        builder.HasData(
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Free, Name = "Free",
                MaxLinkedBots = 1, MaxCallsPerDay = 20, MonthlyPrice = 0m,
            },
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Basic, Name = "Basic",
                MaxLinkedBots = 1, MaxCallsPerDay = 200, MonthlyPrice = 5m,
            },
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Plus, Name = "Plus",
                MaxLinkedBots = 3, MaxCallsPerDay = 1000, MonthlyPrice = 12m,
            },
            new SubscriptionPlan
            {
                Id = SystemPlanIds.Family, Name = "Family",
                MaxLinkedBots = 10, MaxCallsPerDay = 5000, MonthlyPrice = 25m,
            });
    }
}
