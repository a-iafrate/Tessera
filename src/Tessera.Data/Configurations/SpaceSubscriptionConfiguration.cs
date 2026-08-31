using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class SpaceSubscriptionConfiguration : IEntityTypeConfiguration<SpaceSubscription>
{
    public void Configure(EntityTypeBuilder<SpaceSubscription> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PayPalSubscriptionId).IsRequired();
        builder.Property(x => x.Status).IsRequired();

        // The webhook looks a subscription up by this id on every event — it's the only key
        // PayPal's payload gives us.
        builder.HasIndex(x => x.PayPalSubscriptionId).IsUnique();
        builder.HasIndex(x => x.SpaceId);
    }
}
