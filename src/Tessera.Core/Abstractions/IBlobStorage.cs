namespace Tessera.Core.Abstractions;

// One place attachments are written, read, and removed — the database only ever stores the
// blob name this returns/expects (same separation ITokenVault enforces for refresh tokens,
// docs/07-compliance.md), never the file bytes themselves.
public interface IBlobStorage
{
    Task UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct);

    // A time-limited signed URL, not a permanent one — attachments are permission-gated the
    // same as everything else in a space (docs/02-modello-dati.md), so the URL itself must
    // expire rather than being a durable, guessable link.
    Task<string> GetReadUrlAsync(string blobName, TimeSpan validFor, CancellationToken ct);

    Task DeleteAsync(string blobName, CancellationToken ct);
}
