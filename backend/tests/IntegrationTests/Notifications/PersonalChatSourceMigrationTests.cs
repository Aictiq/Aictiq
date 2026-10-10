using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class PersonalChatSourceMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task migration_recovers_legacy_personal_sources_with_dotnet_guid_byte_order_and_preserves_shared_and_test_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await postgres.CreateDatabaseAsync("chat_source_migration");
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
        await migrator.MigrateAsync("20261004121033_ChatNotificationChannels", ct);

        var organizationId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var channelId = Guid.Parse("11234567-89ab-cdef-0123-456789abcdef");
        await using (var channel = new NpgsqlCommand("""
            INSERT INTO notify.user_channels (id, user_id, type, status, consecutive_failures, created_at)
            VALUES (@id, 'recipient', 1, 1, 0, now())
            """, connection))
        {
            channel.Parameters.AddWithValue("id", channelId);
            await channel.ExecuteNonQueryAsync(ct);
        }

        var kinds = new[] { NotificationKind.RunSucceeded, NotificationKind.RunFailed,
            NotificationKind.RunNeedsInput, NotificationKind.Transitioned };
        var expected = new Dictionary<Guid, NotificationKind>();
        foreach (var kind in kinds)
        {
            var notificationId = kind == NotificationKind.RunSucceeded
                ? Guid.Parse("10234567-89ab-cdef-0123-456789abcdef") : Guid.NewGuid();
            var messageId = DeriveLegacyId(notificationId, channelId);
            if (kind == NotificationKind.RunSucceeded)
                // The first eight bytes are reordered by new Guid(byte[]). Casting the
                // SHA256 hex straight to a PostgreSQL uuid would miss this saved row.
                Assert.Equal(Guid.Parse("5915b9ad-a259-3a91-165d-a784e8f5f2da"), messageId);
            await using var notification = new NpgsqlCommand("""
                INSERT INTO notify.notifications (id, organization_id, user_id, event_id, kind, message, created_at)
                VALUES (@id, @organization, 'recipient', @event, @kind, 'Legacy notification', now())
                """, connection);
            notification.Parameters.AddWithValue("id", notificationId);
            notification.Parameters.AddWithValue("organization", organizationId);
            notification.Parameters.AddWithValue("event", Guid.NewGuid());
            notification.Parameters.AddWithValue("kind", (short)kind);
            await notification.ExecuteNonQueryAsync(ct);
            await SeedQueuesAsync(connection, messageId, channelId, null, $"Personal {kind}", ct);
            expected.Add(messageId, kind);
        }

        var testMessageId = Guid.NewGuid();
        await SeedQueuesAsync(connection, testMessageId, channelId, null, "Settings test message", ct);
        var sharedMessageId = Guid.NewGuid();
        var sharedChannelId = Guid.NewGuid();
        await SeedQueuesAsync(connection, sharedMessageId, sharedChannelId, organizationId, "Shared Factory update", ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        var outbox = await db.ChatOutbox.AsNoTracking().ToDictionaryAsync(row => row.Id, ct);
        var digest = await db.ChatDigestEntries.AsNoTracking().ToDictionaryAsync(row => row.Id, ct);
        Assert.Equal(expected.Count + 2, outbox.Count);
        Assert.Equal(expected.Count + 2, digest.Count);
        foreach (var (id, kind) in expected)
        {
            Assert.Equal(organizationId, outbox[id].SourceOrganizationId);
            Assert.Equal(kind, outbox[id].Kind);
            Assert.Equal($"Personal {kind}", outbox[id].Text);
            Assert.Equal(EmailStatus.Pending, outbox[id].Status);
            Assert.Null(outbox[id].OrganizationId);
            Assert.Equal(organizationId, digest[id].SourceOrganizationId);
            Assert.Equal(kind, digest[id].Kind);
            Assert.Equal($"Personal {kind}", digest[id].Line);
            Assert.Null(digest[id].OrganizationId);
        }
        foreach (var id in new[] { testMessageId, sharedMessageId })
        {
            Assert.Null(outbox[id].SourceOrganizationId);
            Assert.Null(outbox[id].Kind);
            Assert.Equal(EmailStatus.Pending, outbox[id].Status);
            Assert.Null(digest[id].SourceOrganizationId);
            Assert.Null(digest[id].Kind);
        }
        Assert.Equal("Settings test message", outbox[testMessageId].Text);
        Assert.Equal("Settings test message", digest[testMessageId].Line);
        Assert.Equal(organizationId, outbox[sharedMessageId].OrganizationId);
        Assert.Equal(organizationId, digest[sharedMessageId].OrganizationId);
        Assert.Equal("Shared Factory update", outbox[sharedMessageId].Text);
        Assert.Equal("Shared Factory update", digest[sharedMessageId].Line);
    }

    private static Guid DeriveLegacyId(Guid notificationId, Guid channelId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"chat:{notificationId}:{channelId}"))[..16]);

    private static async Task SeedQueuesAsync(NpgsqlConnection connection, Guid id, Guid channelId,
        Guid? organizationId, string text, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO notify.chat_outbox (id, channel_id, organization_id, text, status, attempts, send_after, created_at)
            VALUES (@id, @channel, @organization, @text, 'pending', 0, now(), now());
            INSERT INTO notify.chat_digest_entries (id, channel_id, organization_id, line, created_at)
            VALUES (@id, @channel, @organization, @text, now());
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("channel", channelId);
        command.Parameters.Add("organization", NpgsqlDbType.Uuid).Value = (object?)organizationId ?? DBNull.Value;
        command.Parameters.AddWithValue("text", text);
        await command.ExecuteNonQueryAsync(ct);
    }
}
