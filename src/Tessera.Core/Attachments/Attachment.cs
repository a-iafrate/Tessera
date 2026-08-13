using Tessera.Core.Spaces;

namespace Tessera.Core.Attachments;

// One shared shape for every attachable file, whatever it's attached to — a note today,
// eventually a receipt (docs/06-roadmap.md Fase 4: "Scontrini via vision"). Resource +
// OwnerEntityId is a generic pointer (e.g. ResourceKind.Notes + Note.Id), not a typed FK — same
// reasoning as CalendarSpaceMapping.ExternalCalendarId: EF-owned navigations aren't worth it
// for a lookup that's always by explicit id anyway, and it avoids one table per attachable
// resource.
public class Attachment
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public ResourceKind Resource { get; set; }
    public Guid OwnerEntityId { get; set; }

    // The key AzureBlobStorage actually reads/writes/deletes by — the database never holds the
    // file bytes themselves, only this reference (docs/07-compliance.md).
    public string BlobName { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }

    // No FK to User, same reasoning as Note.CreatedByUserId (docs/02-modello-dati.md, hard
    // rule 3) — the account may be deleted later; resolve names via ResolveActorName.
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}
