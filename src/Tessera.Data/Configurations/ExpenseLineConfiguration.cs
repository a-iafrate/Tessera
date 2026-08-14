using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class ExpenseLineConfiguration : IEntityTypeConfiguration<ExpenseLine>
{
    public void Configure(EntityTypeBuilder<ExpenseLine> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RawText).IsRequired();
        builder.Property(x => x.NormalizedName).IsRequired();
        builder.Property(x => x.Price).HasPrecision(18, 2);

        // Price-history lookups (docs/06-roadmap.md "Storico prezzi") match by product name
        // across every expense in the space.
        builder.HasIndex(x => x.NormalizedName);
    }
}
