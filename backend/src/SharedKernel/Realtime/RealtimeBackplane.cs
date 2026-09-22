using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Aictiq.SharedKernel.Realtime;

/// <summary>Realtime fan-out selection. Redis is for deployments that outgrow Postgres LISTEN/NOTIFY.</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    [RegularExpression("^(postgres|redis|none)$", ErrorMessage = "Realtime:Backplane must be postgres, redis, or none.")]
    public string Backplane { get; init; } = "postgres";

    /// <summary>Connection string used only when <see cref="Backplane"/> is redis.</summary>
    public string? RedisConnectionString { get; init; }
}

/// <summary>Small, refetch-oriented wire envelope. It contains identifiers, never item bodies.
/// Exactly one of <see cref="ProjectId"/>, <see cref="UserId"/> and <see cref="RunId"/> names the group.</summary>
public sealed record RealtimeEnvelope(Guid? ProjectId, string? UserId, Guid? RunId, string EventName, JsonElement Payload);

/// <summary>Postgres channel dedicated to realtime. Kept separate from authorization-cache invalidation.</summary>
public static class RealtimeBackplane
{
    public const string Channel = "aictiq_rt";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Realtime for a process that has no SignalR hub - Workers. Integration events are
    /// handled there, not in the API, so an item change or a notification raised by an
    /// outbox handler has to cross a process boundary to reach a browser. NOTIFY is that
    /// crossing in both backplane modes: under postgres every API replica listens, under
    /// redis one replica forwards into SignalR's own backplane. With "none" the null
    /// publisher stays, as in the API.
    /// </summary>
    public static IServiceCollection AddRealtimeNotifyPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
        if (string.Equals(options.Backplane, "none", StringComparison.OrdinalIgnoreCase)) return services;
        services.RemoveAll<IRealtimePublisher>();
        services.RemoveAll<IUserRealtimePublisher>();
        services.RemoveAll<IRunRealtimePublisher>();
        services.AddSingleton<PostgresRealtimePublisher>();
        services.AddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        services.AddSingleton<IUserRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        services.AddSingleton<IRunRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        return services;
    }
}

/// <summary>Publishes through Postgres so every API instance, including this one, fans out uniformly.</summary>
public sealed class PostgresRealtimePublisher(NpgsqlDataSource dataSource) : IRealtimePublisher, IUserRealtimePublisher, IRunRealtimePublisher
{
    public async Task PublishAsync(Guid projectId, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        await PublishAsync(new RealtimeEnvelope(projectId, null, null, eventName, JsonSerializer.SerializeToElement(payload, RealtimeBackplane.Json)), cancellationToken);
    }

    public Task PublishToUserAsync(string userId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        PublishAsync(new RealtimeEnvelope(null, userId, null, eventName, JsonSerializer.SerializeToElement(payload, RealtimeBackplane.Json)), cancellationToken);

    public Task PublishToRunAsync(Guid runId, string eventName, object payload, CancellationToken cancellationToken = default) =>
        PublishAsync(new RealtimeEnvelope(null, null, runId, eventName, JsonSerializer.SerializeToElement(payload, RealtimeBackplane.Json)), cancellationToken);

    private async Task PublishAsync(RealtimeEnvelope notification, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Serialize(notification, RealtimeBackplane.Json);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT pg_notify('{RealtimeBackplane.Channel}', @payload)", connection);
        command.Parameters.AddWithValue("payload", envelope);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
