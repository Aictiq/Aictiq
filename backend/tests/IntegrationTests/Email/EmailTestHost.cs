using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Aictiq.Modules.Identity;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// The Workers process's email wiring, against a real migrated database - the same shape
/// as OutboxProcessorTests. No HTTP: sending is a background concern and the API has no
/// part in it beyond owning the migration.
/// </summary>
public sealed class EmailTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly NpgsqlDataSource _dataSource;

    private EmailTestHost(ServiceProvider provider, NpgsqlDataSource dataSource)
    {
        _provider = provider;
        _dataSource = dataSource;
    }

    public OutboxProcessor Outbox => _provider.GetRequiredService<OutboxProcessor>();
    public EmailDeliveryService Delivery => _provider.GetRequiredService<EmailDeliveryService>();
    public IServiceProvider Services => _provider;

    /// <param name="settings">
    /// Email configuration. Empty means an instance with no relay - which is a supported
    /// deployment, so it is also a case worth testing.
    /// </param>
    public static async Task<EmailTestHost> CreateAsync(
        PostgresFixture postgres, string dbPrefix, Dictionary<string, string?>? settings = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(dbPrefix);
        var dataSource = NpgsqlDataSource.Create(connectionString);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(dataSource);
        services.AddSharedKernel();
        services.AddEmail(configuration);
        services.AddSingleton<ICurrentUser, SystemCurrentUser>();
        // Identity owns the shared outbox table, so its context must migrate first.
        services.AddModuleDbContext<IdentityDbContext>("identity");
        services.AddNotificationsModule();
        services.AddNotificationsWorkers(configuration);
        services.AddOutboxProcessor(typeof(SendEmailRequested).Assembly);

        var provider = services.BuildServiceProvider();
        await MigrationRunner.MigrateAsync(
            provider, [typeof(IdentityDbContext), typeof(NotificationsDbContext)]);

        return new EmailTestHost(provider, dataSource);
    }

    /// <summary>Raises the event the way a module would: an outbox row in its own transaction.</summary>
    public async Task<Guid> EnqueueAsync(SendEmailRequested request, CancellationToken cancellationToken)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.Set<OutboxMessage>().Add(OutboxMessage.From(request));
        await db.SaveChangesAsync(cancellationToken);
        return request.EventId;
    }

    public async Task<T> QueryAsync<T>(
        Func<NotificationsDbContext, CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        using var scope = _provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(), cancellationToken);
    }

    /// <summary>Makes every pending row claimable now, standing in for the backoff elapsing.</summary>
    public async Task ExpireBackoffAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE notify.email_outbox SET send_after = now() - interval '1 hour' WHERE status = 'pending'",
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Clears the processed mark, standing in for a crash before it landed.</summary>
    public async Task ReplayOutboxAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE shared.outbox_messages SET processed_at = NULL", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _dataSource.DisposeAsync();
    }
}
