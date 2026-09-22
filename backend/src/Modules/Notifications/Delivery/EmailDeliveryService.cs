using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// Drains <c>notify.email_outbox</c> in the Workers process. Separate from the outbox
/// consumer on purpose: an SMTP relay is a third party that can be slow, rate-limiting or
/// down for an hour, and none of that should stall the delivery of every other
/// integration event in the system.
/// </summary>
/// <remarks>
/// A message is claimed by moving its <c>send_after</c> forward and incrementing its
/// attempt count in one statement, under <c>FOR UPDATE SKIP LOCKED</c>. There is no
/// "sending" state, and that is the point: a process that dies mid-send leaves a row that
/// simply becomes claimable again when its backoff elapses, instead of a row stuck in a
/// state only a human can clear. The cost is that a crash between the relay accepting a
/// message and the row being marked can send it twice - the right trade for email, where
/// a duplicate is a nuisance and a silent loss is a bug report.
/// </remarks>
public sealed class EmailDeliveryService(
    NpgsqlDataSource dataSource,
    IEmailSender sender,
    IOptions<EmailDeliveryOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailDeliveryService> logger) : BackgroundService
{
    public const string MeterName = "Aictiq.Email";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Sent = Meter.CreateCounter<long>(
        "email.sent", unit: "{message}", description: "Emails a relay accepted.");

    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        "email.failed", unit: "{message}", description: "Email send attempts that threw.");

    private static readonly Counter<long> Skipped = Meter.CreateCounter<long>(
        "email.skipped", unit: "{message}",
        description: "Emails dropped because this instance has no relay configured.");

    private readonly EmailDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A fresh database before migrations, a transient outage - try again.
                logger.LogWarning(ex, "Email delivery sweep failed; retrying on the next tick");
            }

            // A full batch means there is more waiting; go straight round again.
            if (claimed < _options.BatchSize)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>One sweep: claim a batch, send it, settle each row. Public so tests can drive it.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);

        foreach (var message in claimed)
        {
            await SettleAsync(message, cancellationToken);
        }

        await PruneAsync(cancellationToken);
        return claimed.Count;
    }

    private sealed record Claimed(
        Guid Id, string To, string Subject, string Html, string Text, string Template, int Attempts);

    private async Task<List<Claimed>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Claiming and scheduling the next attempt are the same statement, so two workers
        // can never take the same row and a crash cannot leave one claimed forever.
        await using var command = new NpgsqlCommand(
            """
            UPDATE notify.email_outbox
            SET attempts = attempts + 1,
                send_after = now() + interval '1 second' * LEAST(
                    @maxRetry::double precision,
                    @baseRetry::double precision * power(2, attempts))
            WHERE id IN (
                SELECT id FROM notify.email_outbox
                WHERE status = 'pending' AND send_after <= now()
                ORDER BY send_after
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING id, to_address, subject, body_html, body_text, template, attempts
            """, connection);
        command.Parameters.AddWithValue("batch", _options.BatchSize);
        command.Parameters.AddWithValue("baseRetry", _options.BaseRetrySeconds);
        command.Parameters.AddWithValue("maxRetry", _options.MaxRetrySeconds);

        var claimed = new List<Claimed>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new Claimed(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
        }

        return claimed;
    }

    private async Task SettleAsync(Claimed message, CancellationToken cancellationToken)
    {
        var tag = new KeyValuePair<string, object?>("email.template", message.Template);

        try
        {
            await sender.SendAsync(
                new EmailMessage(message.To, message.Subject, message.Html, message.Text),
                cancellationToken);

            Sent.Add(1, tag);
            await MarkAsync(message.Id, EmailStatus.Sent, error: null, cancellationToken);
        }
        catch (EmailNotConfiguredException)
        {
            // Not a failure of this message: there is no relay on this instance, and there
            // will not be one before the next attempt. Park it so the queue stays readable
            // and an operator can see what the invitation links should have said.
            Skipped.Add(1, tag);
            await MarkAsync(message.Id, EmailStatus.Skipped,
                "No SMTP relay is configured on this instance.", cancellationToken);

            logger.LogInformation(
                "Email '{Subject}' to {Recipient} was not sent: no SMTP relay is configured",
                message.Subject, message.To);
        }
        catch (Exception ex)
        {
            Failed.Add(1, tag);

            if (message.Attempts >= _options.MaxAttempts)
            {
                await MarkAsync(message.Id, EmailStatus.Failed, ex.Message, cancellationToken);
                logger.LogError(ex,
                    "Email {Id} to {Recipient} failed permanently after {Attempts} attempts",
                    message.Id, message.To, message.Attempts);
            }
            else
            {
                // Still pending: the claim already moved send_after forward by the backoff.
                await RecordErrorAsync(message.Id, ex.Message, cancellationToken);
                logger.LogWarning(ex, "Email {Id} to {Recipient} failed (attempt {Attempts}/{MaxAttempts})",
                    message.Id, message.To, message.Attempts, _options.MaxAttempts);
            }
        }
    }

    private async Task MarkAsync(
        Guid id, string status, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE notify.email_outbox
            SET status = @status,
                sent_at = CASE WHEN @status = 'sent' THEN now() END,
                last_error = @error
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)Truncate(error) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordErrorAsync(Guid id, string error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE notify.email_outbox SET last_error = @error WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("error", Truncate(error)!);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes settled rows past the retention window. <c>failed</c> rows are left where
    /// they are - nobody could deliver them, and that is a thing to look at rather than
    /// history to compact.
    /// </summary>
    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        if (_options.RetentionDays <= 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM notify.email_outbox
            WHERE id IN (
                SELECT id FROM notify.email_outbox
                WHERE status IN ('sent','skipped') AND created_at < @cutoff
                LIMIT @batch)
            """, connection);
        command.Parameters.AddWithValue("cutoff", timeProvider.GetUtcNow().AddDays(-_options.RetentionDays));
        command.Parameters.AddWithValue("batch", _options.BatchSize * 10);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>last_error is bounded in the schema; a relay's rejection can be a paragraph.</summary>
    private static string? Truncate(string? error) =>
        error is { Length: > 1000 } ? error[..1000] : error;
}
