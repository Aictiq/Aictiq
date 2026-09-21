using System.Reflection;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// Sends the deliberately small, operator-enabled adoption report.
///
/// The runtime role must not bypass RLS simply to measure adoption. Tenant-owned table
/// counts are therefore Postgres relation estimates from the catalog; identity and
/// organization totals are exact because those tables have no tenant row to disclose.
/// Nothing in this service persists an installation identifier or sends content.
/// </summary>
public sealed class AnonymousTelemetryReporter(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<AnonymousTelemetryReporter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Telemetry:Enabled"))
        {
            return;
        }

        if (!Uri.TryCreate(configuration["Telemetry:Endpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("Anonymous telemetry is enabled but Telemetry:Endpoint is not an absolute HTTPS URL; no report will be sent");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendAsync(endpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Telemetry must never affect availability or expose an endpoint/config value.
                logger.LogDebug(ex, "Anonymous telemetry report was not delivered");
            }

            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var report = await ReadReportAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(report), Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("Aictiq-AnonymousTelemetry/1");

        using var response = await httpClientFactory
            .CreateClient("AictiqTelemetry")
            .SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TelemetryReport> ReadReportAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM tenancy.organizations),
                (SELECT count(*) FROM identity."AspNetUsers" WHERE is_active AND NOT is_agent),
                (SELECT count(*) FROM identity."AspNetUsers" WHERE is_active AND is_agent),
                COALESCE((SELECT GREATEST(reltuples, 0)::bigint FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'tenancy' AND c.relname = 'projects'), 0),
                COALESCE((SELECT GREATEST(reltuples, 0)::bigint FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'work' AND c.relname = 'items'), 0)
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new TelemetryReport(
            Version: Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Organizations: reader.GetInt64(0),
            HumanAccounts: reader.GetInt64(1),
            AgentAccounts: reader.GetInt64(2),
            Projects: reader.GetInt64(3),
            WorkItems: reader.GetInt64(4));
    }

    private sealed record TelemetryReport(
        string Version,
        long Organizations,
        long HumanAccounts,
        long AgentAccounts,
        long Projects,
        long WorkItems);
}
