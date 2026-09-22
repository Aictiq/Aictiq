namespace Aictiq.SharedKernel.Storage;

/// <summary>Metadata for a stored object, as returned by a HEAD.</summary>
public sealed record BlobMetadata(long ContentLength, string? ContentType, DateTimeOffset LastModified);

/// <summary>An object being read. Disposing <see cref="Content"/> releases the connection to the store.</summary>
public sealed record BlobContent(Stream Content, long ContentLength, string? ContentType);

/// <summary>
/// The application's whole view of object storage.
///
/// Two ways bytes move. Avatars go browser ↔ store directly, with a presigned PUT and GET.
/// Attachments pass through the API (<see cref="PutAsync"/>, <see cref="OpenReadAsync"/>):
/// images are re-encoded on the way in, and the browser never needs to reach the store at
/// all - the attachment is served from an authenticated API route. Either way the API owns
/// the <em>metadata</em> - size, content type, who uploaded it - in Postgres.
///
/// Object keys are hierarchical and tenant-first
/// (<c>org/{orgId}/project/{projectId}/attachments/{attachmentId}/{filename}</c>) so a
/// tenant's objects can be listed, exported or deleted as one prefix.
/// </summary>
public interface IBlobStorage
{
    /// <summary>
    /// A URL the browser can PUT to. <paramref name="contentType"/> is signed into the
    /// URL, so the upload must send exactly that header - a client cannot quietly store
    /// something other than what the API approved.
    /// </summary>
    Task<Uri> PresignUploadAsync(
        string key, string contentType, long maxBytes, TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A URL the browser can GET. <paramref name="fileName"/> sets the download filename
    /// via <c>response-content-disposition</c>, so the stored key never has to be
    /// human-readable.
    /// </summary>
    Task<Uri> PresignDownloadAsync(
        string key, string? fileName = null, TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>Writes an object, replacing any object at that key.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the object is absent. The caller disposes the stream.</summary>
    Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Returns null when the object is absent.</summary>
    Task<BlobMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deleting an object that is not there succeeds - S3 delete is idempotent.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every object under <paramref name="prefix"/>, which must end in <c>/</c> so
    /// <c>org/1</c> can never reach <c>org/12</c>. This is how a deleted project or
    /// organization leaves nothing behind in the store - including objects no row knows
    /// about any more. Returns how many were deleted; a second call deletes nothing.
    /// </summary>
    Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
