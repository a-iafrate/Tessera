using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Shopping;

namespace Tessera.Data.Configurations;

public sealed class ShoppingItemConfiguration : IEntityTypeConfiguration<ShoppingItem>
{
    public void Configure(EntityTypeBuilder<ShoppingItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RawText).IsRequired();
        builder.Property(x => x.NormalizedName).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 3);

        // Hottest query on this entity: showing the list, splitting checked/unchecked.
        builder.HasIndex(x => new { x.ShoppingListId, x.IsChecked });
    }
}
