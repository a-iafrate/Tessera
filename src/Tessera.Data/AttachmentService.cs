using Microsoft.EntityFrameworkCore;
using Tessera.Core.Abstractions;
using Tessera.Core.Attachments;
using Tessera.Core.Spaces;

namespace Tessera.Data;

// Ties IBlobStorage (the bytes) and Attachment (the database row referencing them) together —
// every consumer (notes today, receipts eventually — docs/06-roadmap.md Fase 4) goes through
// this rather than calling IBlobStorage directly, so blob naming and the write order (blob
// first, then the row that points at it — never the other way round, which could leave a row
// pointing at a blob that was never actually written) stay consistent in one place.
public sealed class AttachmentService(TesseraDbContext db, IBlobStorage blobStorage)
{
    public async Task<Attachment> AddAsync(
        Guid spaceId, ResourceKind resource, Guid ownerEntityId, Guid uploadedByUserId,
        Stream content, string fileName, string contentType, long sizeBytes, CancellationToken ct)
    {
        var blobName = $"{spaceId}/{Guid.NewGuid()}-{fileName}";
        await blobStorage.UploadAsync(blobName, content, contentType, ct);

        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId,
            Resource = resource,
            OwnerEntityId = ownerEntityId,
            BlobName = blobName,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            UploadedByUserId = uploadedByUserId,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return attachment;
    }

    public async Task<IReadOnlyList<Attachment>> GetForAsync(ResourceKind resource, Guid ownerEntityId, CancellationToken ct) =>
        await db.Attachments
            .Where(x => x.Resource == resource && x.OwnerEntityId == ownerEntityId)
            .OrderBy(x => x.UploadedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    // Generated fresh on every request, never stored — a SAS URL is only as safe as its
    // expiry, and the shortest expiry that still works is "long enough for this one page view
    // or bot reply," not "however long the attachment exists."
    public async Task<string?> GetReadUrlAsync(Guid attachmentId, TimeSpan validFor, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId, ct);
        return attachment is null ? null : await blobStorage.GetReadUrlAsync(attachment.BlobName, validFor, ct);
    }

    // Called whenever the owning resource itself is deleted (a note, eventually an expense) —
    // Attachment has no foreign key to cascade from, same reasoning as every other Guid-only
    // reference in this codebase (docs/02-modello-dati.md), so the cleanup is explicit. Blobs
    // are removed before the rows, not after: an interrupted delete leaves an orphaned blob
    // (harmless, just wasted storage) rather than a row pointing at nothing.
    public async Task DeleteAllForAsync(ResourceKind resource, Guid ownerEntityId, CancellationToken ct)
    {
        var attachments = await db.Attachments.Where(x => x.Resource == resource && x.OwnerEntityId == ownerEntityId).ToListAsync(ct);
        foreach (var attachment in attachments)
        {
            await blobStorage.DeleteAsync(attachment.BlobName, ct);
        }

        db.Attachments.RemoveRange(attachments);
        await db.SaveChangesAsync(ct);
    }
}
