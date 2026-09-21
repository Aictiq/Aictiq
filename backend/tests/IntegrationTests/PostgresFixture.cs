using Npgsql;
using Testcontainers.PostgreSql;

namespace Aictiq.IntegrationTests;

/// <summary>
/// One Postgres container per test run; each test class gets its own database so
/// classes can run in parallel without stepping on each other's schema.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Per-database pool ceiling. xUnit builds the class — and therefore a whole API — once
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
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

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

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
