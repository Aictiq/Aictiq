using System.ComponentModel.DataAnnotations;

namespace Aictiq.SharedKernel.Storage;

/// <summary>
/// Configuration for the object store, bound from the <c>S3</c> section.
///
/// The application only ever speaks the S3 API, so Garage (dev and self-host), MinIO,
/// AWS S3 and Cloudflare R2 are all reachable by changing these values and nothing else.
/// See <c>docs/self-host.md</c>.
/// </summary>
public sealed class S3StorageOptions
{
    public const string SectionName = "S3";

    /// <summary>Where the API and Workers reach the store - usually an internal address.</summary>
    [Required]
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// The address a <em>browser</em> uses. Presigned URLs are signed for this host, so it
    /// must be what the browser actually connects to: SigV4 covers the Host header, and a
    /// proxy that rewrites it invalidates the signature. Defaults to
    /// <see cref="Endpoint"/> when the two are the same.
    /// </summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>
    /// A path prefix the browser's requests carry but the store never sees, because a
    /// reverse proxy strips it (Caddy's <c>handle_path /s3/*</c>).
    ///
    /// It cannot simply be part of <see cref="PublicEndpoint"/>: the AWS SDK would then
    /// sign the prefixed path, while the store - which receives the stripped one -
    /// verifies against the bare path, and every signature would fail. So the URL is
    /// signed <em>without</em> the prefix and the prefix is inserted afterwards, which
    /// leaves both sides signing the same path.
    ///
    /// Empty when the store has its own hostname or port, which needs no rewriting.
    /// </summary>
    public string? PublicPathPrefix { get; set; }

    [Required]
    public string Bucket { get; set; } = "";

    [Required]
    public string AccessKey { get; set; } = "";

    [Required]
    public string SecretKey { get; set; } = "";

    /// <summary>Garage's region name. Meaningless to Garage itself but part of the signature.</summary>
    public string Region { get; set; } = "garage";

    /// <summary>
    /// Path-style addressing (<c>host/bucket/key</c>). Required for Garage and MinIO,
    /// which do not do virtual-hosted-style buckets on a bare host or IP.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>How long a presigned upload URL stays valid.</summary>
    public int UploadUrlTtlMinutes { get; set; } = 15;

    /// <summary>
    /// How long a presigned download URL stays valid. Short on purpose: the URL is the
    /// credential, and it will end up in browser history and referrer headers.
    /// </summary>
    public int DownloadUrlTtlMinutes { get; set; } = 5;

    public string ResolvedPublicEndpoint =>
        string.IsNullOrWhiteSpace(PublicEndpoint) ? Endpoint : PublicEndpoint;

    /// <summary>Normalised to "/prefix" (leading slash, no trailing slash), or empty.</summary>
    public string ResolvedPublicPathPrefix =>
        string.IsNullOrWhiteSpace(PublicPathPrefix)
            ? ""
            : "/" + PublicPathPrefix.Trim('/');
}
