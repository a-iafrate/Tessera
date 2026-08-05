using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Users;

namespace Tessera.Data.Configurations;

public sealed class ChannelIdentityConfiguration : IEntityTypeConfiguration<ChannelIdentity>
{
    public void Configure(EntityTypeBuilder<ChannelIdentity> builder)
    {
        builder.HasKey(x => x.Id);

        // Lookup on every inbound message — the hottest path in the pipeline.
        builder.HasIndex(x => new { x.ChannelName, x.ExternalUserId }).IsUnique();
    }
}
