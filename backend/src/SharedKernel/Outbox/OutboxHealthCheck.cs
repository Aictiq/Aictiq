using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Aictiq.SharedKernel.Outbox;

/// <summary>
/// Reports dead-lettered outbox messages on /health/ready. Degraded (not Unhealthy) on
/// purpose: the process is working, but integration events have been permanently dropped
/// and someone has to look. Alert on this - a poison message must never rot in silence.
/// </summary>
public sealed class OutboxHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public const string Name = "outbox";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM shared.outbox_messages WHERE dead_lettered_at IS NOT NULL",
                connection);
            var deadLettered = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

            return deadLettered == 0
                ? HealthCheckResult.Healthy("No dead-lettered outbox messages.")
                : HealthCheckResult.Degraded(
                    $"{deadLettered} outbox message(s) dead-lettered; their integration events were never delivered.",
                    data: new Dictionary<string, object> { ["deadLettered"] = deadLettered });
        }
        catch (Exception ex)
        {
            // A missing table (pre-migration startup) or an outage is a readiness signal,
            // not a crash - the caller's other checks decide the overall verdict.
            return HealthCheckResult.Degraded("Could not read the outbox dead-letter count.", ex);
        }
    }
}
