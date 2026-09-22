using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Storage;

/// <summary>
/// <see cref="IBlobStorage"/> over the S3 API.
///
/// Two clients on purpose. Presigning is a local computation over the URL the browser
/// will call, and SigV4 signs the Host header - so a URL signed against the server's
/// internal address is invalid the moment a browser resolves the store by a different
/// name. <see cref="_presigner"/> is therefore configured with
/// <see cref="S3StorageOptions.ResolvedPublicEndpoint"/>, while <see cref="_client"/>
/// talks to the internal one for HEAD and DELETE.
/// </summary>
public sealed class S3BlobStorage : IBlobStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly IAmazonS3 _presigner;
    private readonly S3StorageOptions _options;
    private readonly bool _ownsPresigner;

    public S3BlobStorage(IOptions<S3StorageOptions> options)
    {
        _options = options.Value;
        _client = S3ClientFactory.Create(_options, _options.Endpoint);

        // When the two endpoints are the same there is nothing to keep apart.
        _ownsPresigner = _options.ResolvedPublicEndpoint != _options.Endpoint;
        _presigner = _ownsPresigner
            ? S3ClientFactory.Create(_options, _options.ResolvedPublicEndpoint)
            : _client;
    }

    /// <summary>
    /// The presigned PUT fixes the content type, so a client cannot store something other
    /// than what the API approved.
    ///
    /// <paramref name="maxBytes"/> is <b>not</b> enforced by the signature: a presigned
    /// PUT has no equivalent of a POST policy's <c>content-length-range</c>, and signing an
    /// exact <c>Content-Length</c> would require knowing the size in advance. The caller
    /// must therefore treat the limit as a post-condition - <see cref="HeadAsync"/> the
    /// object before recording its metadata and <see cref="DeleteAsync"/> it if it is over.
    /// The value is validated here so a caller cannot pass a meaningless bound.
    /// </summary>
    public Task<Uri> PresignUploadAsync(
        string key, string contentType, long maxBytes, TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(_options.UploadUrlTtlMinutes)),
        };

        return Task.FromResult(Presign(request));
    }

    public Task<Uri> PresignDownloadAsync(
        string key, string? fileName = null, TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(_options.DownloadUrlTtlMinutes)),
        };

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            // Lets the stored key stay opaque while the browser still saves a sane name.
            request.ResponseHeaderOverrides.ContentDisposition =
                $"attachment; filename=\"{SanitiseFileName(fileName)}\"";
        }

        return Task.FromResult(Presign(request));
    }

    /// <summary>
    /// AWS SDK v4 writes <c>https</c> into every presigned URL regardless of the
    /// configured <c>ServiceURL</c> scheme (and regardless of <c>UseHttp</c>), which is
    /// wrong for a plain-HTTP store - a local Garage, or one behind a proxy that
    /// terminates TLS. Rewriting the scheme is safe: SigV4 signs the method, path, query
    /// and the <c>host</c> header, never the scheme, so the signature still verifies.
    /// </summary>
    private Uri Presign(GetPreSignedUrlRequest request)
    {
        var url = new Uri(_presigner.GetPreSignedURL(request));
        var signed = new UriBuilder(url);
        var target = new Uri(_options.ResolvedPublicEndpoint);

        if (!string.Equals(signed.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            // An explicit port came from ServiceURL and is both what the browser must
            // connect to and what SigV4 put in the signed host header, so it is kept. A
            // default one has to stay implicit: UriBuilder materialises it (443 for the
            // SDK's https), and carrying that into an http URL would point the browser
            // at port 443 and make it send `Host: host:443`, which the signature - taken
            // over the bare host - does not cover.
            var port = url.IsDefaultPort ? -1 : url.Port;
            signed.Scheme = target.Scheme;
            signed.Port = port;
        }

        // Added after signing on purpose - see S3StorageOptions.PublicPathPrefix. The
        // proxy strips it again before the store sees the request, so both ends sign the
        // same path.
        var prefix = _options.ResolvedPublicPathPrefix;
        if (prefix.Length > 0)
        {
            signed.Path = prefix + signed.Path;
        }

        return signed.Uri;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        await HeadAsync(key, cancellationToken) is not null;

    public async Task<BlobMetadata?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var response = await _client.GetObjectMetadataAsync(
                _options.Bucket, key, cancellationToken);

            return new BlobMetadata(
                response.ContentLength,
                response.Headers.ContentType,
                response.LastModified ?? DateTime.UtcNow);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A HEAD on a missing key is an answer, not a failure.
            return null;
        }
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken);
    }

    public async Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket, key, cancellationToken);
            return new BlobContent(response.ResponseStream, response.ContentLength, response.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _client.DeleteObjectAsync(_options.Bucket, key, cancellationToken);
    }

    public async Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!prefix.EndsWith('/') || prefix.Trim('/').Length == 0)
        {
            throw new ArgumentException("A prefix must name a folder, such as org/{id}/.", nameof(prefix));
        }

        var deleted = 0;
        // Always the first page: what the previous round listed is gone, so the listing
        // shrinks until it is empty - no continuation token to go stale under concurrent deletes.
        while (true)
        {
            var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = prefix,
                MaxKeys = 1000,
            }, cancellationToken);

            var keys = page.S3Objects?.Select(o => new KeyVersion { Key = o.Key }).ToList() ?? [];
            if (keys.Count == 0)
            {
                return deleted;
            }

            var response = await _client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _options.Bucket,
                Objects = keys,
                Quiet = true,
            }, cancellationToken);

            if (response.DeleteErrors is { Count: > 0 } errors)
            {
                throw new InvalidOperationException(
                    $"The object store refused to delete {errors.Count} object(s) under {prefix}: {errors[0].Code} {errors[0].Message}");
            }

            deleted += keys.Count;
        }
    }

    /// <summary>
    /// Strips what would break - or forge - the Content-Disposition header. Quotes and
    /// control characters (a newline especially) would let a filename inject a header.
    /// </summary>
    private static string SanitiseFileName(string fileName)
    {
        var cleaned = new string([.. fileName.Where(c => !char.IsControl(c) && c is not ('"' or '\\'))]);
        return cleaned.Length == 0 ? "download" : cleaned;
    }

    public void Dispose()
    {
        _client.Dispose();
        if (_ownsPresigner)
        {
            _presigner.Dispose();
        }
    }
}
