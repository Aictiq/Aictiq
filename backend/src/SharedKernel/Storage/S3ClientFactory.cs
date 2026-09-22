using Amazon.Runtime;
using Amazon.S3;

namespace Aictiq.SharedKernel.Storage;

/// <summary>
/// Builds the S3 clients. One place, because the settings that make an S3-compatible
/// store work at all are easy to get subtly wrong and even easier to get wrong in only
/// one of two constructors.
/// </summary>
internal static class S3ClientFactory
{
    /// <param name="timeout">
    /// Caps how long a call may block. The health check passes a short one: a store that
    /// hangs must make readiness fail quickly, not make the probe itself hang.
    /// </param>
    /// <param name="maxErrorRetry">Retries. Zero for the health check - retrying a dead store just delays the verdict.</param>
    public static AmazonS3Client Create(
        S3StorageOptions options, string endpoint, TimeSpan? timeout = null, int? maxErrorRetry = null)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            // Without this the SDK signs and builds presigned URLs as https even when
            // ServiceURL says http - which is every local Garage and every deployment
            // where TLS is terminated at a proxy in front of the store.
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            // Garage and MinIO serve buckets as a path segment; virtual-hosted style
            // would need wildcard DNS neither of them has by default.
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            // SDK v4 adds CRC trailers to every upload by default; S3-compatible stores
            // (Garage, MinIO, R2) do not all accept them. Checksums only where S3 requires one.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        if (timeout is not null)
        {
            config.Timeout = timeout;
        }

        if (maxErrorRetry is not null)
        {
            config.MaxErrorRetry = maxErrorRetry.Value;
        }

        return new AmazonS3Client(options.AccessKey, options.SecretKey, config);
    }
}
