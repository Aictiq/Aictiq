using Aictiq.Modules.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// RLS is intentionally tested through a raw app-role connection. EF's query filter is
/// useful ergonomics, but it must not be what prevents a hand-written SQL query from
/// reading another organization.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class PostgresRlsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Acme = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Globex = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private string _connectionString = null!;
    private string _login = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync("postgres_rls");
        _login = $"aictiq_rls_{Guid.NewGuid():N}";

        // This is the one module migration test needs. The production API first applies
        // Identity's role migration; a focused module test creates the shared group role
        // explicitly so it can exercise Tenancy's real RLS migration in isolation.
        await using (var admin = new NpgsqlConnection(_connectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(admin, """
                DO $$
                BEGIN
                    CREATE ROLE aictiq_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
                EXCEPTION WHEN duplicate_object OR unique_violation THEN
                    NULL;
                END $$;
                """);
        }

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(_connectionString, postgres => postgres.MigrationsHistoryTable("__ef_migrations_history", "tenancy"))
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var context = new TenancyDbContext(options))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using var seed = new NpgsqlConnection(_connectionString);
        await seed.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(seed, $"CREATE ROLE \"{_login}\" LOGIN PASSWORD 'rls-test-password' IN ROLE aictiq_app;");
        await ExecuteAsync(seed, """
            INSERT INTO tenancy.organizations (id, slug, name, plan, settings, created_by, created_at, updated_at)
            VALUES
                ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'acme', 'Acme', 'self_hosted', '{}'::jsonb, 'test', now(), now()),
                ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'globex', 'Globex', 'self_hosted', '{}'::jsonb, 'test', now(), now());
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at)
            VALUES
                ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'acme-user', 0, now()),
                ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'globex-user', 0, now());
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_connectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"DROP ROLE IF EXISTS \"{_login}\";");
    }

    [Fact]
    public async Task an_unfiltered_app_role_query_fails_closed_and_cannot_read_another_organization()
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = _login,
            Password = "rls-test-password",
            Pooling = false,
        };
        await using var app = new NpgsqlConnection(builder.ConnectionString);
        await app.OpenAsync(TestContext.Current.CancellationToken);

        // No EF filter and no app.org_id: the database itself returns no tenant rows.
        Assert.Equal(0L, await ScalarAsync<long>(app,
            "SELECT count(*) FROM tenancy.organization_members"));

        await ExecuteAsync(app,
            "SELECT set_config('app.org_id', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', false);");
        var users = await ReadStringsAsync(app,
            "SELECT user_id FROM tenancy.organization_members ORDER BY user_id");

        Assert.Equal(["acme-user"], users);
        Assert.DoesNotContain("globex-user", users);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<List<string>> ReadStringsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) values.Add(reader.GetString(0));
        return values;
    }
}
