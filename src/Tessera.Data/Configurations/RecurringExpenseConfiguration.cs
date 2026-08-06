using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class RecurringExpenseConfiguration : IEntityTypeConfiguration<RecurringExpense>
{
    public void Configure(EntityTypeBuilder<RecurringExpense> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Description).IsRequired();

        builder.OwnsOne(x => x.Recurrence);

        // The generation job's idempotency check (docs/02-modello-dati.md) — a later
        // checklist item, but the index belongs here alongside the schema.
        builder.HasIndex(x => new { x.SpaceId, x.LastGeneratedFor });
    }
}
