using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class UsageEventConfiguration : IEntityTypeConfiguration<UsageEvent>
{
    public void Configure(EntityTypeBuilder<UsageEvent> builder)
    {
        builder.HasKey(x => x.Id);

        // The exact lookup UsageService does on every L3 call: count of today's rows for one
        // space.
        builder.HasIndex(x => new { x.SpaceId, x.OccurredAt });
    }
}
