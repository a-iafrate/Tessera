using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MonthlyLimit).HasPrecision(18, 2);

        // CategoryId null = budget complessivo (docs/02-modello-dati.md). EF Core emits this
        // as a filtered index (WHERE CategoryId IS NOT NULL) since the column is nullable, so
        // it only dedupes per-category budgets — BudgetService.SetAsync's find-or-create is
        // what actually caps a space at one overall budget.
        builder.HasIndex(x => new { x.SpaceId, x.CategoryId }).IsUnique();
    }
}
