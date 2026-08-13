using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Tessera.Core.Abstractions;

namespace Tessera.Data;

// Only registered when BlobStorage:ConnectionString is configured (Program.cs mirrors the Key
// Vault conditional-registration pattern) — attachments simply can't be stored until an
// account exists. Takes the account-level BlobServiceClient rather than a BlobContainerClient
// directly: it's the same client shared with the rest of the app, and the container is cheap
// to derive from it on every call.
public sealed class AzureBlobStorage(BlobServiceClient serviceClient) : IBlobStorage
{
    private const string ContainerName = "attachments";

    private BlobContainerClient Container => serviceClient.GetBlobContainerClient(ContainerName);

    public async Task UploadAsync(string blobName, Stream content, string contentType, CancellationToken ct)
    {
        var blob = Container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
        }, ct);
    }

    // An account-key SAS: the BlobServiceClient is built from a connection string that embeds
    // the account key (docs/07-compliance.md — the key itself only ever lives in Key Vault /
    // user-secrets, never logged), so BlobClient can sign the SAS itself without a round trip
    // to fetch a user-delegation key.
    public Task<string> GetReadUrlAsync(string blobName, TimeSpan validFor, CancellationToken ct)
    {
        var blob = Container.GetBlobClient(blobName);
        var sasUri = blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(validFor));
        return Task.FromResult(sasUri.ToString());
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct)
    {
        await Container.DeleteBlobIfExistsAsync(blobName, cancellationToken: ct);
    }
}
