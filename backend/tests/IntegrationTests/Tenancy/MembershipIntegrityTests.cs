using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// The last-Owner guarantee, asserted with the application bypassed entirely.
///
/// The endpoint refuses the obvious cases for the sake of the error message; this trigger
/// is the guarantee, and it has to hold for a migration script, a support engineer at a
/// psql prompt, and - the case no application check can cover - two transactions that
/// cannot see each other's work.
/// </summary>
[Trait("Category", "Tenancy")]
[Collection("postgres")]
public sealed class MembershipIntegrityTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "member_integrity");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task the_last_owner_cannot_be_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await SeedAsync(connection, "sole-owner", [("alice", 0)], ct);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection,
                "DELETE FROM tenancy.organization_members WHERE organization_id = @org AND user_id = 'alice'",
                organizationId, ct));

        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Equal(DatabaseSignals.LastOwner, error.MessageText);
    }

    [Fact]
    public async Task the_last_owner_cannot_be_demoted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await SeedAsync(connection, "demoted", [("alice", 0), ("bob", 1)], ct);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection,
                "UPDATE tenancy.organization_members SET role = 1 WHERE organization_id = @org AND user_id = 'alice'",
                organizationId, ct));

        Assert.Equal(DatabaseSignals.LastOwner, error.MessageText);
    }

    [Fact]
    public async Task one_of_two_owners_may_go()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await SeedAsync(connection, "two-owners", [("alice", 0), ("bob", 0)], ct);

        await ExecuteAsync(connection,
            "DELETE FROM tenancy.organization_members WHERE organization_id = @org AND user_id = 'bob'",
            organizationId, ct);

        Assert.Equal(1L, await CountAsync(connection, organizationId, ct));
    }

    [Fact]
    public async Task removing_someone_who_is_not_an_owner_is_none_of_the_triggers_business()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await SeedAsync(connection, "ordinary", [("alice", 0), ("bob", 2)], ct);

        await ExecuteAsync(connection,
            "DELETE FROM tenancy.organization_members WHERE organization_id = @org AND user_id = 'bob'",
            organizationId, ct);

        Assert.Equal(1L, await CountAsync(connection, organizationId, ct));
    }

    [Fact]
    public async Task two_owners_demoting_each_other_at_the_same_moment_leave_one_standing()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var setup = await OpenAsync(ct);
        var organizationId = await SeedAsync(setup, "simultaneous", [("alice", 0), ("bob", 0)], ct);

        await using var first = await OpenAsync(ct);
        await using var second = await OpenAsync(ct);
        await using var firstTransaction = await first.BeginTransactionAsync(ct);
        await using var secondTransaction = await second.BeginTransactionAsync(ct);

        // Alice demotes Bob. Her trigger sees Alice still an Owner and lets it through,
        // holding the lock on the organization row until she commits.
        await ExecuteAsync(first,
            "UPDATE tenancy.organization_members SET role = 1 WHERE organization_id = @org AND user_id = 'bob'",
            organizationId, ct, firstTransaction);

        // Bob demotes Alice at the same time. Read-committed hides Alice's uncommitted
        // work from him, so any check the *application* could make would pass here too -
        // this statement blocks inside the trigger instead, waiting for that lock.
        var bob = ExecuteAsync(second,
            "UPDATE tenancy.organization_members SET role = 1 WHERE organization_id = @org AND user_id = 'alice'",
            organizationId, ct, secondTransaction);

        await firstTransaction.CommitAsync(ct);

        var error = await Assert.ThrowsAsync<PostgresException>(() => bob);
        Assert.Equal(DatabaseSignals.LastOwner, error.MessageText);
        await secondTransaction.RollbackAsync(ct);

        await using var check = new NpgsqlCommand(
            "SELECT count(*) FROM tenancy.organization_members WHERE organization_id = @org AND role = 0",
            setup);
        check.Parameters.AddWithValue("org", organizationId);
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task purging_an_organization_is_not_blocked_by_its_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        var organizationId = await SeedAsync(connection, "purge-me", [("alice", 0)], ct);

        // The cascade deletes the Owner's membership row too. Refusing that would make an
        // organization impossible to purge - the guard has to notice that there
        // is no longer anything to own.
        await ExecuteAsync(connection, "DELETE FROM tenancy.organizations WHERE id = @org", organizationId, ct);

        Assert.Equal(0L, await CountAsync(connection, organizationId, ct));
    }

    // --------------------------------------------------------------------------- helpers

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<Guid> SeedAsync(
        NpgsqlConnection connection, string slug, (string UserId, short Role)[] members, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await using (var organization = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organizations
                (id, slug, name, plan, settings, created_by, created_at, updated_at)
            VALUES (@id, @slug, @slug, 'self_hosted', '{"timeZone":"UTC","weekStart":1}'::jsonb,
                    'seed', now(), now())
            """, connection))
        {
            organization.Parameters.AddWithValue("id", id);
            organization.Parameters.AddWithValue("slug", slug);
            await organization.ExecuteNonQueryAsync(ct);
        }

        foreach (var (userId, role) in members)
        {
            await using var member = new NpgsqlCommand(
                """
                INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
                VALUES (@org, @user, @role, now(), @role <> 3)
                """, connection);
            member.Parameters.AddWithValue("org", id);
            member.Parameters.AddWithValue("user", userId);
            member.Parameters.AddWithValue("role", role);
            await member.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, Guid organizationId, CancellationToken ct,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("org", organizationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, Guid organizationId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenancy.organization_members WHERE organization_id = @org", connection);
        command.Parameters.AddWithValue("org", organizationId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
