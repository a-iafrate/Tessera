using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Spaces;

namespace Tessera.Data.Configurations;

public sealed class MembershipPermissionConfiguration : IEntityTypeConfiguration<MembershipPermission>
{
    public void Configure(EntityTypeBuilder<MembershipPermission> builder)
    {
        builder.HasKey(x => new { x.MembershipId, x.Resource });
    }
}
