using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.SharedKernel.Persistence;

public static class MigrationRunner
{
    // Arbitrary but stable key - every process that migrates this database must use it.
    private const long AdvisoryLockKey = 0x544D_504C_5431; // "TMPLT1"

    /// <summary>
    /// Applies migrations for the given contexts under a Postgres advisory lock, so
    /// concurrent starts (API restarts, API + Workers racing) can never run migrations
    /// in parallel. Only the API process calls this; Workers wait for the API.
    /// </summary>
    public static async Task MigrateAsync(
        IServiceProvider services, IReadOnlyList<Type> contextTypes, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var adminConnectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetConnectionString("appdb-admin");
        // Development/test hosts often deliberately have only one local superuser
        // connection. Production supplies appdb-admin; normal request traffic never does.
        await using var adminDataSource = string.IsNullOrWhiteSpace(adminConnectionString)
            ? null
            : NpgsqlDataSource.Create(adminConnectionString);
        var migrationDataSource = adminDataSource ?? dataSource;

        await using var connection = await migrationDataSource.OpenConnectionAsync(cancellationToken);
        await using (var takeLock = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            takeLock.Parameters.AddWithValue("key", AdvisoryLockKey);
            await takeLock.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            foreach (var contextType in contextTypes)
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
                // Resolving a context from DI still gives it the request-path appdb data
                // source. Point it at this already-open administrative connection before
                // calling Migrate; otherwise the advisory lock would be admin-only while
                // DDL accidentally ran as aictiq_app and failed (or, worse, widened the
                // app role's authority just to make migration work).
                context.Database.SetDbConnection(connection, contextOwnsConnection: false);
                await context.Database.MigrateAsync(cancellationToken);
            }
        }
        finally
        {
            await using var releaseLock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            releaseLock.Parameters.AddWithValue("key", AdvisoryLockKey);
            await releaseLock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
