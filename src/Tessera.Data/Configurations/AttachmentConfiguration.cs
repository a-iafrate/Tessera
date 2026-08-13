using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tessera.Core.Attachments;

namespace Tessera.Data.Configurations;

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BlobName).IsRequired();
        builder.Property(x => x.FileName).IsRequired();
        builder.Property(x => x.ContentType).IsRequired();

        // The exact lookup every consumer does: "attachments for this note/expense/whatever".
        builder.HasIndex(x => new { x.Resource, x.OwnerEntityId });
    }
}
