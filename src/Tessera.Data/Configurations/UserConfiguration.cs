using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Users;

namespace Tessera.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email).IsRequired();
        builder.Property(x => x.PreferredCulture).IsRequired();

        // Backfills existing rows to the same default as new ones (docs/02-modello-dati.md).
        builder.Property(x => x.DigestHourLocal).HasDefaultValue(8);

        builder.HasMany(x => x.ChannelIdentities)
            .WithOne()
            .HasForeignKey(x => x.UserId);

        builder.HasMany(x => x.Memberships)
            .WithOne()
            .HasForeignKey(x => x.UserId);
    }
}
