using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class SpaceConfiguration : IEntityTypeConfiguration<Space>
{
    public void Configure(EntityTypeBuilder<Space> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.Currency).IsRequired();

        builder.HasMany(x => x.Memberships)
            .WithOne()
            .HasForeignKey(x => x.SpaceId);

        // Default backfills existing rows when this column lands on a populated table —
        // the app always sets PlanId explicitly at creation regardless.
        builder.Property(x => x.PlanId).HasDefaultValue(SystemPlanIds.Free);

        // Existing rows predate this column and were all created at registration — true is
        // the correct backfill, not just a placeholder.
        builder.Property(x => x.IsPersonal).HasDefaultValue(true);

        builder.HasOne<SubscriptionPlan>()
            .WithMany()
            .HasForeignKey(x => x.PlanId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
