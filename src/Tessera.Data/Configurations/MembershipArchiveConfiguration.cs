using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class MembershipArchiveConfiguration : IEntityTypeConfiguration<MembershipArchive>
{
    public void Configure(EntityTypeBuilder<MembershipArchive> builder)
    {
        builder.HasKey(x => new { x.SpaceId, x.UserId });
        builder.Property(x => x.DisplayNameSnapshot).IsRequired();
    }
}
