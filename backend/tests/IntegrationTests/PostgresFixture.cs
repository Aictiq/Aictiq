using Npgsql;
using Testcontainers.PostgreSql;

// One Postgres container for the whole test run. An assembly fixture rather than a
// collection fixture: a collection fixture would also have to put every test class in
// that one collection, and xUnit runs a collection's classes one after another - which
// is what made the suite take forty minutes. Each test gets its own database, so there
// is nothing for parallel classes to step on. See GarageFixture for the same reasoning.
[assembly: AssemblyFixture(typeof(Aictiq.IntegrationTests.PostgresFixture))]

namespace Aictiq.IntegrationTests;

/// <summary>
/// One Postgres container per test run; each test class gets its own database so
/// classes can run in parallel without stepping on each other's schema.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Per-database pool ceiling. xUnit builds the class - and therefore a whole API - once
    /// per <em>test</em>, and every one of those holds a pool plus two LISTEN connections
    /// for the cache invalidators. Left at Npgsql's default of 100 each, a few dozen
    /// parallel tests exhaust the server's slots and the suite fails with
    /// <c>53300: sorry, too many clients already</c> in whichever class happened to be
    /// starting. Eight is far more than any single test needs.
    /// </summary>
    private const int MaxPoolSizePerDatabase = 8;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        // The other half of the same problem: the default of 100 is a production-shaped
        // number, and this container serves the whole suite at once.
        .WithCommand("-c", "max_connections=400")
        // The data directory in RAM: the suite creates hundreds of databases. (fsync,
        // full_page_writes and synchronous_commit are already off - Testcontainers' default.)
        .WithTmpfsMount("/var/lib/postgresql/data")
        // Parallel queries put their shared memory in /dev/shm, which Docker caps at 64 MB.
        // With the whole suite on one server that fills up, and a migration fails with
        // 53100: could not resize shared memory segment ... No space left on device.
        .WithCreateParameterModifier(parameters => (parameters.HostConfig ??= new()).ShmSize = 512L * 1024 * 1024)
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await SharedHosts.DisposeAllAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The fully migrated, unseeded database every API host clones. Building it is a whole
    /// API start - every module's migrations - which used to happen once per test; a clone
    /// is a file copy. Built once, on first use, by whoever asks first.
    /// </summary>
    private const string TemplateDatabase = "aictiq_template";

    private readonly SemaphoreSlim _templateLock = new(1, 1);
    private bool _templateReady;

    /// <summary>
    /// A new database that is a copy of the migrated template. <paramref name="migrate"/>
    /// builds the template on the first call only: it gets the template's connection string
    /// and must leave no connection open to it.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync(string prefix, Func<string, Task> migrate)
    {
        if (!_templateReady)
        {
            await _templateLock.WaitAsync();
            try
            {
                if (!_templateReady)
                {
                    await ExecuteAsync($"CREATE DATABASE \"{TemplateDatabase}\"");
                    await migrate(ConnectionStringFor(TemplateDatabase));
                    NpgsqlConnection.ClearAllPools();
                    // A database can only be copied while nobody is connected to it; the
                    // host that migrated it may have left LISTEN or pooled sessions behind.
                    // ALLOW_CONNECTIONS false keeps it that way for the rest of the run.
                    await ExecuteAsync($"""
                        ALTER DATABASE "{TemplateDatabase}" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false;
                        SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{TemplateDatabase}';
                        """);
                    _templateReady = true;
                }
            }
            finally
            {
                _templateLock.Release();
            }
        }

        var name = $"{prefix}_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\" STRATEGY {CloneStrategy}");
        return ConnectionStringFor(name);
    }

    /// <summary>Overridable for measuring: AICTIQ_TEST_CLONE_STRATEGY=WAL_LOG|FILE_COPY.</summary>
    private static readonly string CloneStrategy =
        Environment.GetEnvironmentVariable("AICTIQ_TEST_CLONE_STRATEGY") is { Length: > 0 } strategy ? strategy : "FILE_COPY";

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            MaxPoolSize = MaxPoolSizePerDatabase,
        }.ConnectionString;

    /// <summary>An empty database, for tests that run the migrations themselves.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        // xunit instantiates the test class (and so InitializeAsync) once per test,
        // so database names must be unique per call.
        var name = $"{prefix}_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,
            MaxPoolSize = MaxPoolSizePerDatabase,
        };
        return builder.ConnectionString;
    }
}
