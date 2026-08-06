using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Expenses;

namespace Tessera.Data.Configurations;

public sealed class PendingExpenseConfirmationConfiguration : IEntityTypeConfiguration<PendingExpenseConfirmation>
{
    public void Configure(EntityTypeBuilder<PendingExpenseConfirmation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CandidateAsGrouped).HasPrecision(18, 2);
        builder.Property(x => x.CandidateAsDecimal).HasPrecision(18, 2);
    }
}
