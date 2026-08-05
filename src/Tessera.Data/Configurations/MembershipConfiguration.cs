using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.SpaceId, x.UserId }).IsUnique();

        builder.HasMany(x => x.Permissions)
            .WithOne()
            .HasForeignKey(x => x.MembershipId);
    }
}
