using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Aictiq.Modules.Tenancy.Access;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// Keeps every API instance's view of who belongs where honest.
///
/// Membership and organization lookups are cached because the tenant middleware reads
/// them on every organization-scoped request. That makes their invalidation a
/// correctness concern rather than a performance one: an instance that missed a
/// deletion would go on admitting people to an organization that no longer exists until
/// its entry aged out. The writer NOTIFYs, every instance purges the tags for that
/// organization, and the five-minute expiry is only the backstop for a missed message.
/// </summary>
public sealed class TenancyCacheInvalidator(
    NpgsqlDataSource dataSource,
    HybridCache cache,
    ILogger<TenancyCacheInvalidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, args) =>
                {
                    _ = PurgeAsync(args.Payload, stoppingToken);
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {TenancyCache.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LISTEN {Channel} interrupted; reconnecting in 5 s", TenancyCache.Channel);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The payload is parsed by <see cref="TenancyCache.ChangeSignal"/>, which is also
    /// what formats it — a malformed one purges nothing rather than guessing.
    /// </summary>
    private async Task PurgeAsync(string payload, CancellationToken cancellationToken)
    {
        if (TenancyCache.ChangeSignal.TryParse(payload) is not { } signal)
        {
            logger.LogWarning("Ignoring malformed {Channel} payload", TenancyCache.Channel);
            return;
        }

        try
        {
            await TenancyCache.EvictAsync(cache, signal, cancellationToken);
            logger.LogDebug(
                "Tenancy cache purged for organization {OrganizationId}, project {ProjectId} after NOTIFY",
                signal.OrganizationId, signal.ProjectId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purging the tenancy cache for {OrganizationId} failed", signal.OrganizationId);
        }
    }
}
