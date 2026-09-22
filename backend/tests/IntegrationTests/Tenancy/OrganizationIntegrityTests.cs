using Npgsql;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// The tenancy invariants, asserted against the database with the application bypassed
/// entirely. Endpoint validation exists for the error message; these constraints are the
/// guarantee, and they have to hold when the code above them is wrong.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class OrganizationIntegrityTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "tenancy_integrity");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<Guid> InsertOrganizationAsync(
        NpgsqlConnection connection, string slug, string name, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organizations
                (id, slug, name, plan, settings, created_by, created_at, updated_at)
            VALUES (@id, @slug, @name, 'self_hosted', '{"timeZone":"UTC","weekStart":1}'::jsonb,
                    'seed', now(), now())
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("a")]
    public async Task the_slug_shape_is_enforced_by_a_check_constraint(string slug)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertOrganizationAsync(connection, slug, "Whatever", ct));

        Assert.Equal("ck_organizations_slug_format", error.ConstraintName);
    }

    [Fact]
    public async Task a_blank_name_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertOrganizationAsync(connection, "blank-name", "   ", ct));

        Assert.Equal("ck_organizations_name_not_blank", error.ConstraintName);
    }

    [Fact]
    public async Task two_organizations_cannot_share_an_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        await InsertOrganizationAsync(connection, "contested", "First", ct);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertOrganizationAsync(connection, "contested", "Second", ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Fact]
    public async Task a_membership_cannot_point_at_an_organization_that_is_not_there()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), 'nobody', 0, now())
            """, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task one_person_is_a_member_of_an_organization_at_most_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await InsertOrganizationAsync(connection, "double-member", "Double", ct);

        await AddMemberAsync(connection, organizationId, "alice", 0, ct);

        // Two rows would mean two answers to "what may Alice do here", and the one the
        // authorization filter happened to read first would win.
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => AddMemberAsync(connection, organizationId, "alice", 1, ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }

    [Fact]
    public async Task purging_an_organization_takes_its_memberships_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await InsertOrganizationAsync(connection, "purged", "Purged", ct);
        await AddMemberAsync(connection, organizationId, "alice", 0, ct);

        // The API only ever soft-deletes; this is the hard delete a purge job
        // performs, and it must not leave memberships pointing into space.
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM tenancy.organizations WHERE id = @id", connection))
        {
            delete.Parameters.AddWithValue("id", organizationId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM tenancy.organization_members WHERE organization_id = @id", connection);
        count.Parameters.AddWithValue("id", organizationId);

        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
    }

    private static async Task AddMemberAsync(
        NpgsqlConnection connection, Guid organizationId, string userId, short role, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", role);
        await command.ExecuteNonQueryAsync(ct);
    }
}
