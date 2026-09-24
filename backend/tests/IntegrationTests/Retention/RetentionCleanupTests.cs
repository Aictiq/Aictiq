using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.Modules.Identity.Workers;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Retention;

/// <summary>
/// The Workers-side retention sweep. Rows are seeded and asserted with raw SQL because
/// the point is what the tables look like afterwards, not what any endpoint returns.
/// </summary>
public sealed class RetentionCleanupTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async ValueTask InitializeAsync()
    {
        // The API migrates on start, so the schema exists by the time this returns.
        _context = await ApiTestContext.CreateAsync(postgres, garage, "retention");
        _dataSource = NpgsqlDataSource.Create(_context.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _context.DisposeAsync();
    }

    private RetentionCleanupService Service(RetentionOptions options) =>
        new(_dataSource, Options.Create(options), TimeProvider.System,
            NullLogger<RetentionCleanupService>.Instance);

    private async Task<long> CountAsync(string sql, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task spent_refresh_tokens_are_purged_and_live_ones_are_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO identity.refresh_tokens (id, user_id, token_hash, family_id, created_at, expires_at, used_at, revoked_at)
            VALUES
              -- consumed and old: purge
              (gen_random_uuid(), 'u1', 'hash-consumed-old', gen_random_uuid(), now() - interval '90 days', now() + interval '1 day', now() - interval '89 days', NULL),
              -- revoked and old: purge
              (gen_random_uuid(), 'u1', 'hash-revoked-old',  gen_random_uuid(), now() - interval '90 days', now() + interval '1 day', NULL, now() - interval '89 days'),
              -- never used, long expired: purge
              (gen_random_uuid(), 'u1', 'hash-expired-old',  gen_random_uuid(), now() - interval '90 days', now() - interval '80 days', NULL, NULL),
              -- consumed but RECENT: keep, the rotation chain is still evidence
              (gen_random_uuid(), 'u1', 'hash-consumed-new', gen_random_uuid(), now() - interval '1 day',  now() + interval '13 days', now(), NULL),
              -- old row that is still usable: keep, age alone never revokes a live token
              (gen_random_uuid(), 'u1', 'hash-live-old',     gen_random_uuid(), now() - interval '90 days', now() + interval '30 days', NULL, NULL)
            """, ct);

        await Service(new RetentionOptions { RefreshTokenDays = 30, AuditLogDays = 0, ProcessedOutboxDays = 0 })
            .RunOnceAsync(ct);

        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM identity.refresh_tokens WHERE token_hash LIKE '%-old' AND token_hash <> 'hash-live-old'", ct));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM identity.refresh_tokens WHERE token_hash = 'hash-consumed-new'", ct));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM identity.refresh_tokens WHERE token_hash = 'hash-live-old'", ct));
    }

    [Fact]
    public async Task audit_rows_past_the_retention_window_are_purged()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO audit.audit_log (id, entity_type, entity_id, field, old_value, new_value, user_id, at)
            VALUES
              (gen_random_uuid(), 'Note', 'n1', 'Title', 'a', 'b', 'u1', now() - interval '400 days'),
              (gen_random_uuid(), 'Note', 'n2', 'Title', 'a', 'b', 'u1', now() - interval '10 days')
            """, ct);

        await Service(new RetentionOptions { RefreshTokenDays = 0, AuditLogDays = 365, ProcessedOutboxDays = 0 })
            .RunOnceAsync(ct);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM audit.audit_log WHERE entity_id = 'n1'", ct));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM audit.audit_log WHERE entity_id = 'n2'", ct));

        // Retention does not open the door: the append-only guarantee still holds for
        // everything except the bounded purge function. n2 is still there, so these
        // statements really do reach the trigger.
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var rawDelete = new NpgsqlCommand("DELETE FROM audit.audit_log", connection);
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => rawDelete.ExecuteNonQueryAsync(ct));
        Assert.Contains("append-only", deleteEx.Message);

        // Calling the purge function first does not hand the caller a general licence:
        // the flag is cleared before it returns, and UPDATE is refused under any flag.
        // The cutoff is prehistoric so the purge itself deletes nothing.
        await using var afterPurge = new NpgsqlCommand(
            """
            SELECT shared.purge_audit_log(now() - interval '1000 years', 100);
            DELETE FROM audit.audit_log;
            """, connection);
        var leakEx = await Assert.ThrowsAsync<PostgresException>(() => afterPurge.ExecuteNonQueryAsync(ct));
        Assert.Contains("append-only", leakEx.Message);

        await using var rawUpdate = new NpgsqlCommand(
            "UPDATE audit.audit_log SET new_value = 'rewritten'", connection);
        var updateEx = await Assert.ThrowsAsync<PostgresException>(() => rawUpdate.ExecuteNonQueryAsync(ct));
        Assert.Contains("append-only", updateEx.Message);
    }

    [Fact]
    public async Task processed_outbox_rows_are_purged_but_dead_lettered_ones_are_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO shared.outbox_messages (id, type, payload, occurred_at, processed_at, attempts, dead_lettered_at)
            VALUES
              (gen_random_uuid(), 'Done.Old',     '{}', now() - interval '30 days', now() - interval '30 days', 0, NULL),
              (gen_random_uuid(), 'Done.Recent',  '{}', now() - interval '1 day',   now() - interval '1 day',   0, NULL),
              (gen_random_uuid(), 'Pending.Old',  '{}', now() - interval '30 days', NULL,                       0, NULL),
              (gen_random_uuid(), 'Poison.Old',   '{}', now() - interval '30 days', NULL,                      10, now() - interval '30 days')
            """, ct);

        await Service(new RetentionOptions { RefreshTokenDays = 0, AuditLogDays = 0, ProcessedOutboxDays = 7 })
            .RunOnceAsync(ct);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE type = 'Done.Old'", ct));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE type = 'Done.Recent'", ct));
        // Undelivered work is never collected as garbage.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE type = 'Pending.Old'", ct));
        // A dead letter is an open incident, not history.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shared.outbox_messages WHERE type = 'Poison.Old'", ct));
    }

    [Fact]
    public async Task a_retention_of_zero_days_disables_that_purge()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO audit.audit_log (id, entity_type, entity_id, field, old_value, new_value, user_id, at)
            VALUES (gen_random_uuid(), 'Note', 'keep-forever', 'Title', 'a', 'b', 'u1', now() - interval '4000 days')
            """, ct);

        await Service(new RetentionOptions { RefreshTokenDays = 0, AuditLogDays = 0, ProcessedOutboxDays = 0 })
            .RunOnceAsync(ct);

        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM audit.audit_log WHERE entity_id = 'keep-forever'", ct));
    }

    [Fact]
    public async Task deletes_are_batched_so_a_large_backlog_never_runs_as_one_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteAsync(
            """
            INSERT INTO audit.audit_log (id, entity_type, entity_id, field, old_value, new_value, user_id, at)
            SELECT gen_random_uuid(), 'Note', 'batch', 'Title', 'a', 'b', 'u1', now() - interval '400 days'
            FROM generate_series(1, 25)
            """, ct);

        // BatchSize below the row count forces the sweep to loop; it must still finish.
        await Service(new RetentionOptions
        {
            RefreshTokenDays = 0, AuditLogDays = 365, ProcessedOutboxDays = 0, BatchSize = 10
        }).RunOnceAsync(ct);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM audit.audit_log WHERE entity_id = 'batch'", ct));
    }
}
