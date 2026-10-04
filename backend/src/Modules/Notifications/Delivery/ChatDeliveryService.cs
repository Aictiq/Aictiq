using System.Security.Cryptography;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// Drains <c>notify.chat_outbox</c> in the Workers process, claiming rows the way
/// <see cref="EmailDeliveryService"/> does: the claim moves <c>send_after</c> forward by the
/// backoff, so a crash mid-send just leaves the row to be retried.
/// </summary>
/// <remarks>
/// A channel that fails <see cref="ChatDeliveryOptions.MaxAttempts"/> times in a row - a
/// deleted webhook, a bot blocked in its chat - is marked <see cref="ChatChannelStatus.Broken"/>
/// and its queued messages are skipped; every other channel carries on. A successful test
/// message from settings brings it back. Logs name the channel id, never its target.
/// </remarks>
public sealed class ChatDeliveryService(
    NpgsqlDataSource dataSource, IServiceScopeFactory scopes, IOptions<ChatDeliveryOptions> options,
    TimeProvider clock, ILogger<ChatDeliveryService> logger) : BackgroundService
{
    private readonly ChatDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try { claimed = await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Chat delivery sweep failed; retrying on the next tick"); }
            if (claimed < _options.BatchSize)
            {
                try { await Task.Delay(_options.PollInterval, clock, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private sealed record Claimed(Guid Id, Guid ChannelId, Guid? OrganizationId, string Text, int Attempts);

    /// <summary>One sweep: claim a batch, send it, settle each row. Public so tests can drive it.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        foreach (var message in claimed)
        {
            await using var scope = scopes.CreateAsyncScope();
            await SettleAsync(scope.ServiceProvider, message, cancellationToken);
        }
        await PruneAsync(cancellationToken);
        return claimed.Count;
    }

    private async Task<List<Claimed>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE notify.chat_outbox
            SET attempts = attempts + 1,
                send_after = now() + interval '1 second' * LEAST(
                    @maxRetry::double precision,
                    @baseRetry::double precision * power(2, attempts))
            WHERE id IN (
                SELECT id FROM notify.chat_outbox
                WHERE status = 'pending' AND send_after <= now()
                ORDER BY send_after
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING id, channel_id, organization_id, text, attempts
            """, connection);
        command.Parameters.AddWithValue("batch", _options.BatchSize);
        command.Parameters.AddWithValue("baseRetry", _options.BaseRetrySeconds);
        command.Parameters.AddWithValue("maxRetry", _options.MaxRetrySeconds);
        var claimed = new List<Claimed>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            claimed.Add(new Claimed(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3), reader.GetInt32(4)));
        return claimed;
    }

    private async Task SettleAsync(IServiceProvider services, Claimed claimed, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<NotificationsDbContext>();
        using var tenant = claimed.OrganizationId is { } organizationId
            ? services.GetRequiredService<AmbientCurrentTenant>().Use(organizationId) : null;
        var message = await db.ChatOutbox.SingleAsync(m => m.Id == claimed.Id, cancellationToken);
        IChatChannel? channel = claimed.OrganizationId is null
            ? await db.UserChannels.SingleOrDefaultAsync(c => c.Id == claimed.ChannelId, cancellationToken)
            : await db.OrgChannels.SingleOrDefaultAsync(c => c.Id == claimed.ChannelId, cancellationToken);
        if (channel is not { Status: ChatChannelStatus.Active, ProtectedTarget: { } protectedTarget })
        {
            message.Status = EmailStatus.Skipped;
            message.LastError = channel is null ? "The channel was disconnected." : "The channel is not working.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var sender = services.GetServices<IChatSender>().Single(s => s.Type == channel.Type);
        try
        {
            await sender.SendAsync(services.GetRequiredService<ChatSecrets>().Unprotect(protectedTarget), claimed.Text, cancellationToken);
            message.Status = EmailStatus.Sent; message.SentAt = clock.GetUtcNow(); message.LastError = null;
            channel.ConsecutiveFailures = 0; channel.LastError = null;
        }
        catch (CryptographicException)
        {
            // The key that encrypted the target is gone: no retry can read it again.
            message.Status = EmailStatus.Failed; message.LastError = "The channel must be connected again.";
            channel.Status = ChatChannelStatus.Broken; channel.LastError = message.LastError;
            logger.LogWarning("Chat channel {ChannelId} target could not be decrypted; marked broken", channel.Id);
        }
        catch (ChatDeliveryException ex)
        {
            message.LastError = ex.Message;
            channel.ConsecutiveFailures++; channel.LastError = ex.Message;
            if (claimed.Attempts >= _options.MaxAttempts) message.Status = EmailStatus.Failed;
            if (channel.ConsecutiveFailures >= _options.MaxAttempts)
            {
                channel.Status = ChatChannelStatus.Broken;
                logger.LogWarning("Chat channel {ChannelId} marked broken after {Failures} failed sends: {Error}",
                    channel.Id, channel.ConsecutiveFailures, ex.Message);
            }
            else
                logger.LogInformation("Chat message {MessageId} to channel {ChannelId} failed (attempt {Attempts}): {Error}",
                    claimed.Id, channel.Id, claimed.Attempts, ex.Message);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        if (_options.RetentionDays <= 0) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM notify.chat_outbox
            WHERE id IN (
                SELECT id FROM notify.chat_outbox
                WHERE status IN ('sent','skipped') AND created_at < @cutoff
                LIMIT @batch)
            """, connection);
        command.Parameters.AddWithValue("cutoff", clock.GetUtcNow().AddDays(-_options.RetentionDays));
        command.Parameters.AddWithValue("batch", _options.BatchSize * 10);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
