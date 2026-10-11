using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class RunRefinedChannelModeMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task migration_gives_connected_shared_channels_the_refined_kind_at_their_needs_input_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync("run_refined_migration");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var role = new NpgsqlCommand("""
            DO $$
            BEGIN
                CREATE ROLE aictiq_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
            EXCEPTION WHEN duplicate_object OR unique_violation THEN
                NULL;
            END $$;
            """, connection))
            await role.ExecuteNonQueryAsync(ct);

        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connectionString, provider => provider.MigrationsHistoryTable("__ef_migrations_history", "notify"))
            .UseSnakeCaseNamingConvention().Options;
        await using var db = new NotificationsDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261010050231_PersonalChatNotificationSource", ct);

        var organizationId = Guid.NewGuid();
        var channels = new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = """{"11": 0, "12": 1, "13": 1}""",
            [Guid.NewGuid()] = """{"11": 1, "13": 2}""",
            [Guid.NewGuid()] = """{"13": 1, "14": 0}""",
            [Guid.NewGuid()] = """{"0": 1}"""
        };
        foreach (var (id, modes) in channels)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO notify.org_channels (id, organization_id, name, type, status, consecutive_failures, modes, created_at)
                VALUES (@id, @organization, 'Team', 2, 1, 0, @modes::jsonb, now())
                """, connection);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("organization", organizationId);
            insert.Parameters.AddWithValue("modes", modes);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await migrator.MigrateAsync(cancellationToken: ct);
        var migrated = await db.OrgChannels.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(channel => channel.Id, channel => channel.Modes, ct);
        var ids = channels.Keys.ToArray();
        Assert.Equal(EmailNotificationMode.Immediate, migrated[ids[0]][NotificationKind.RunRefined]);
        Assert.Equal(EmailNotificationMode.Off, migrated[ids[0]][NotificationKind.RunSucceeded]);
        Assert.Equal(EmailNotificationMode.Digest, migrated[ids[1]][NotificationKind.RunRefined]);
        Assert.Equal(EmailNotificationMode.Off, migrated[ids[2]][NotificationKind.RunRefined]);
        Assert.False(migrated[ids[3]].ContainsKey(NotificationKind.RunRefined));
    }
}
