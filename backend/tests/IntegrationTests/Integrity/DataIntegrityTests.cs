using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Integrity;

/// <summary>
/// Proves the invariants hold at the DATABASE, bypassing the application entirely with
/// raw SQL - the whole point of DB-enforced integrity is surviving buggy app code.
/// </summary>
[Collection("postgres")]
public sealed class DataIntegrityTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "integrity");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    [Fact]
    public async Task audit_log_rejects_updates_and_deletes_at_the_database_level()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO audit.audit_log (id, entity_type, entity_id, field, old_value, new_value, at)
            VALUES (gen_random_uuid(), 'Test', '1', 'field', NULL, 'v', now())
            """, connection))
        {
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE audit.audit_log SET new_value = 'rewritten history'", connection))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(ct));
            Assert.Contains("append-only", ex.Message);
        }

        await using var delete = new NpgsqlCommand("DELETE FROM audit.audit_log", connection);
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(ct));
        Assert.Contains("append-only", deleteEx.Message);
    }

}
