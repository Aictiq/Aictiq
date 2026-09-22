using Aictiq.Modules.Automation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// The runner secret lookup is a pre-tenant read, like an invitation link. Proven against the
/// app role with raw SQL: without a tenant nothing is visible, and the hash capability admits
/// exactly the row it names - never its neighbours, never another organization's runners.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunnerRlsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AcmeHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string GlobexHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly Guid Acme = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Globex = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private string _connectionString = null!;
    private string _login = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync("runner_rls");
        _login = $"aictiq_rls_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_connectionString))
        {
            await admin.OpenAsync(Ct);
            await ExecuteAsync(admin, """
                DO $$
                BEGIN
                    CREATE ROLE aictiq_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
                EXCEPTION WHEN duplicate_object OR unique_violation THEN
                    NULL;
                END $$;
                """);
        }

        var options = new DbContextOptionsBuilder<AutomationDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AutomationDbContext.SchemaName))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var context = new AutomationDbContext(options))
        {
            await context.Database.MigrateAsync(Ct);
        }

        await using var seed = new NpgsqlConnection(_connectionString);
        await seed.OpenAsync(Ct);
        await ExecuteAsync(seed, $"CREATE ROLE \"{_login}\" LOGIN PASSWORD 'rls-test-password' IN ROLE aictiq_app;");
        await ExecuteAsync(seed, $"""
            INSERT INTO automation.runners (id, name, token_hash, token_prefix, registered_by, created_at, updated_at, organization_id)
            VALUES
                (gen_random_uuid(), 'acme-box', '{AcmeHash}', 'aaaaaaaa', 'acme-user', now(), now(), '{Acme}'),
                (gen_random_uuid(), 'globex-box', '{GlobexHash}', 'bbbbbbbb', 'globex-user', now(), now(), '{Globex}');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_connectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"DROP ROLE IF EXISTS \"{_login}\";");
    }

    [Fact]
    public async Task the_secret_hash_admits_exactly_its_own_row_and_no_tenant_admits_nothing()
    {
        await using var app = await OpenAppAsync();

        Assert.Equal(0L, await CountAsync(app, org: "", hash: ""));
        Assert.Equal(["acme-box"], await NamesAsync(app, org: "", hash: AcmeHash));
        Assert.Equal(["acme-box"], await NamesAsync(app, org: Acme.ToString(), hash: ""));
        // A tenant and a capability from different organizations still see only what each admits.
        Assert.Equal(["acme-box", "globex-box"], await NamesAsync(app, org: Acme.ToString(), hash: GlobexHash));
        Assert.Equal(0L, await CountAsync(app, org: "", hash: "CCCC"));

        // The capability is SELECT only: it cannot be used to change the row it finds.
        await SetAsync(app, org: "", hash: AcmeHash);
        await using var update = new NpgsqlCommand("UPDATE automation.runners SET name = 'hijacked'", app);
        Assert.Equal(0, await update.ExecuteNonQueryAsync(Ct));
    }

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = _login,
            Password = "rls-test-password",
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task SetAsync(NpgsqlConnection connection, string org, string hash)
    {
        await using var set = new NpgsqlCommand("SELECT set_config('app.org_id', @org, false), set_config('app.runner_token_hash', @hash, false)", connection);
        set.Parameters.AddWithValue("org", org);
        set.Parameters.AddWithValue("hash", hash);
        await set.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(NpgsqlConnection connection, string org, string hash)
    {
        await SetAsync(connection, org, hash);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM automation.runners", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<List<string>> NamesAsync(NpgsqlConnection connection, string org, string hash)
    {
        await SetAsync(connection, org, hash);
        await using var select = new NpgsqlCommand("SELECT name FROM automation.runners ORDER BY name", connection);
        var names = new List<string>();
        await using var reader = await select.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
