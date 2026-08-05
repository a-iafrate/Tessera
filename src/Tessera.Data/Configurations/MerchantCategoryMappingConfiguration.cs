using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class MerchantCategoryMappingConfiguration : IEntityTypeConfiguration<MerchantCategoryMapping>
{
    public void Configure(EntityTypeBuilder<MerchantCategoryMapping> builder)
    {
        builder.HasKey(x => new { x.SpaceId, x.MerchantNormalized });
    }
}
