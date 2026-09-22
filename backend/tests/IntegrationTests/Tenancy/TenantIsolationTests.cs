using Microsoft.EntityFrameworkCore;
using Npgsql;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// Tenant isolation is the property the whole product rests on: every module after
/// Aictiq stores several customers' data in one database, and a leak here is not a bug
/// but an incident. So these run against real Postgres, seed both tenants' rows with raw
/// SQL (bypassing the app entirely, the way the data-integrity tests do), and then read
/// back through EF.
/// </summary>
[Trait("Category", "Tenancy")]
[Collection("postgres")]
public sealed class TenantIsolationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid Acme = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Globex = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync("tenant_isolation");

        await using var context = ContextFor(null);
        await context.Database.EnsureCreatedAsync();

        await ExecuteAsync(
            """
            INSERT INTO tenant_test.things (id, organization_id, name) VALUES
              (gen_random_uuid(), @acme,   'acme-one'),
              (gen_random_uuid(), @acme,   'acme-two'),
              (gen_random_uuid(), @globex, 'globex-one');
            INSERT INTO tenant_test.global_things (id, name) VALUES (gen_random_uuid(), 'shared');
            """,
            ("acme", Acme), ("globex", Globex));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task a_tenant_sees_only_its_own_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var acme = ContextFor(Acme);
        var names = await acme.Things.Select(t => t.Name).OrderBy(n => n).ToListAsync(ct);

        Assert.Equal(["acme-one", "acme-two"], names);
    }

    /// <summary>
    /// The filter closes over the context, and EF caches the model across context
    /// instances - so a filter that captured the *first* context's tenant would still pass
    /// a single-tenant test and leak in production. Two contexts, two tenants, one model.
    /// </summary>
    [Fact]
    public async Task two_contexts_with_different_tenants_do_not_share_a_cached_filter()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var acme = ContextFor(Acme);
        Assert.Equal(2, await acme.Things.CountAsync(ct));

        await using var globex = ContextFor(Globex);
        var globexNames = await globex.Things.Select(t => t.Name).ToListAsync(ct);

        Assert.Equal(["globex-one"], globexNames);

        // And back again, in case the first read is what got cached.
        Assert.Equal(2, await acme.Things.CountAsync(ct));
    }

    /// <summary>
    /// Changing the tenant on a live context must take effect immediately - Workers move
    /// between tenants message by message on one scope.
    /// </summary>
    [Fact]
    public async Task changing_the_tenant_on_a_live_context_changes_what_it_sees()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new AmbientCurrentTenant();

        await using var context = CreateContext(tenant);

        using (tenant.Use(Acme))
        {
            Assert.Equal(2, await context.Things.CountAsync(ct));
        }

        using (tenant.Use(Globex))
        {
            Assert.Equal(1, await context.Things.CountAsync(ct));
        }

        // The scope restored the previous (unset) tenant, which must show nothing rather
        // than everything.
        Assert.Equal(0, await context.Things.CountAsync(ct));
    }

    [Fact]
    public async Task no_tenant_means_no_rows_rather_than_all_rows()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var context = ContextFor(null);

        // Fails closed: a code path that forgets to establish a tenant reads nothing.
        Assert.Equal(0, await context.Things.CountAsync(ct));
        // ...but the data really is there, so the assertion above is not vacuous.
        Assert.Equal(3, await context.Things.IgnoreQueryFilters().CountAsync(ct));
    }

    [Fact]
    public async Task rows_that_are_not_tenant_scoped_are_untouched_by_the_filter()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var context = ContextFor(null);
        Assert.Equal(1, await context.GlobalThings.CountAsync(ct));
    }

    [Fact]
    public async Task a_tenant_cannot_read_another_tenants_row_by_id()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var globex = ContextFor(Globex);
        var globexRowId = await globex.Things.Select(t => t.Id).SingleAsync(ct);

        await using var acme = ContextFor(Acme);
        // 404 rather than 403 at the endpoint level starts here: the row is simply not
        // visible, so the endpoint has nothing to forbid access to.
        Assert.Null(await acme.Things.FirstOrDefaultAsync(t => t.Id == globexRowId, ct));
    }

    [Fact]
    public async Task a_new_row_is_stamped_with_the_current_tenant()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var acme = ContextFor(Acme);
        acme.Things.Add(new TenantThing { Name = "stamped" });
        await acme.SaveChangesAsync(ct);

        var saved = await acme.Things.SingleAsync(t => t.Name == "stamped", ct);
        Assert.Equal(Acme, saved.OrganizationId);
    }

    [Fact]
    public async Task saving_a_tenant_row_with_no_tenant_in_scope_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var context = ContextFor(null);
        context.Things.Add(new TenantThing { Name = "orphan" });

        // Unreachable data at best, a leak at worst - it must fail loudly at the call
        // site rather than land in the table.
        var ex = await Assert.ThrowsAsync<TenantMissingException>(() => context.SaveChangesAsync(ct));
        Assert.Contains("no organization is in scope", ex.Message);
    }

    [Fact]
    public async Task writing_a_row_for_another_tenant_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var acme = ContextFor(Acme);
        acme.Things.Add(new TenantThing { Name = "smuggled", OrganizationId = Globex });

        var ex = await Assert.ThrowsAsync<TenantMissingException>(() => acme.SaveChangesAsync(ct));
        Assert.Contains("must never cross a tenant boundary", ex.Message);

        // And nothing was written.
        await using var globex = ContextFor(Globex);
        Assert.False(await globex.Things.AnyAsync(t => t.Name == "smuggled", ct));
    }

    [Fact]
    public async Task the_same_name_can_be_used_by_two_tenants()
    {
        var ct = TestContext.Current.CancellationToken;

        // Tenant-scoped uniqueness includes the organization, so one customer cannot deny
        // another the use of a name.
        await using var globex = ContextFor(Globex);
        globex.Things.Add(new TenantThing { Name = "acme-one" });
        await globex.SaveChangesAsync(ct);

        Assert.Equal(2, await globex.Things.CountAsync(ct));
    }

    private TenantTestDbContext ContextFor(Guid? tenant)
    {
        var ambient = new AmbientCurrentTenant();
        ambient.Set(tenant);
        return CreateContext(ambient);
    }

    private TenantTestDbContext CreateContext(ICurrentTenant tenant) =>
        new(
            new DbContextOptionsBuilder<TenantTestDbContext>()
                .UseNpgsql(_connectionString)
                .UseSnakeCaseNamingConvention()
                .Options,
            tenant);

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
