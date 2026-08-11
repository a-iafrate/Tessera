using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Users;

namespace Tessera.Data.Configurations;

public sealed class LinkedAccountConfiguration : IEntityTypeConfiguration<LinkedAccount>
{
    public void Configure(EntityTypeBuilder<LinkedAccount> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ProviderUserId).IsRequired();
        builder.Property(x => x.TokenSecretName).IsRequired();

        // One account per provider per user — re-linking updates the existing row rather
        // than accumulating duplicates.
        builder.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
    }
}
