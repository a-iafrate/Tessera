using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Conversations;

namespace Tessera.Data.Configurations;

public sealed class LastOperationConfiguration : IEntityTypeConfiguration<LastOperation>
{
    public void Configure(EntityTypeBuilder<LastOperation> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.OperationType).IsRequired();
        builder.Property(x => x.UndoPayloadJson).IsRequired();
    }
}
