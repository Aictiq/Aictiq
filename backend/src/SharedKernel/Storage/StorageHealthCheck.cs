using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Storage;

/// <summary>
/// Reports the object store on <c>/health/ready</c>.
///
/// Unhealthy, not Degraded: with no store the service cannot accept an attachment or an
/// avatar, and a fresh Garage node whose layout was never applied answers every S3 call
/// with an error - that is exactly the state a readiness probe exists to keep traffic away
/// from. A HEAD on the bucket is the cheapest call that proves credentials, addressing
/// style and the bucket itself all line up.
/// </summary>
public sealed class StorageHealthCheck(IOptions<S3StorageOptions> options) : IHealthCheck, IDisposable
{
    public const string Name = "storage";

    private readonly S3StorageOptions _options = options.Value;
    /// <summary>
    /// A store that is merely slow is a store that is down as far as readiness is
    /// concerned, and a probe that hangs is worse than one that fails: it ties up a
    /// request and leaves the orchestrator waiting on its own timeout.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly AmazonS3Client _client = S3ClientFactory.Create(
        options.Value, options.Value.Endpoint, ProbeTimeout, maxErrorRetry: 0);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The SDK timeout covers a slow response; this also covers a connection that
        // never completes, which the SDK's timeout does not always interrupt.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);

        try
        {
            await _client.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = _options.Bucket }, deadline.Token);

            return HealthCheckResult.Healthy($"Bucket '{_options.Bucket}' is reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"Object storage at {_options.Endpoint} did not answer within {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Object storage at {_options.Endpoint} is not usable (bucket '{_options.Bucket}').", ex);
        }
    }

    public void Dispose() => _client.Dispose();
}
