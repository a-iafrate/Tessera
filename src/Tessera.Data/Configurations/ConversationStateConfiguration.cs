using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Conversations;

namespace Tessera.Data.Configurations;

public sealed class ConversationStateConfiguration : IEntityTypeConfiguration<ConversationState>
{
    public void Configure(EntityTypeBuilder<ConversationState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StateJson).IsRequired();

        // One row per user, upserted (docs/02-modello-dati.md) — never two active states.
        builder.HasIndex(x => x.UserId).IsUnique();
    }
}
