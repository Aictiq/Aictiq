using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Events;

namespace Aictiq.Modules.Notifications.Events;

/// <summary>
/// Runs in the Workers process when the OutboxProcessor delivers
/// <see cref="SendEmailRequested"/>. Renders the template and queues the result; it never
/// touches a relay, so a slow or absent SMTP server cannot hold up the outbox sweep or
/// burn an outbox message's retry budget on something the delivery service is better
/// placed to retry.
/// </summary>
/// <remarks>
/// Idempotent because the queued row's primary key <em>is</em> the event's id. Outbox
/// delivery is at least once; on a replay the insert collides and the handler treats that
/// as success, exactly as NoteCreatedHandler does with its unique index. The guarantee is
/// the database's, not a read-then-check in here.
/// </remarks>
public sealed class SendEmailRequestedHandler(
    NotificationsDbContext db,
    EmailTemplateRenderer renderer,
    TimeProvider timeProvider,
    ILogger<SendEmailRequestedHandler> logger) : IDomainEventHandler<SendEmailRequested>
{
    public async Task HandleAsync(SendEmailRequested domainEvent, CancellationToken cancellationToken)
    {
        var rendered = renderer.Render(domainEvent.Template, domainEvent.Variables);
        var now = timeProvider.GetUtcNow();

        db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Id = domainEvent.EventId,
            ToAddress = domainEvent.To,
            Template = domainEvent.Template,
            Subject = rendered.Subject,
            BodyHtml = rendered.Html,
            BodyText = rendered.Text,
            SendAfter = now,
            CreatedAt = now
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Redelivery of an event already queued - the outcome is what we wanted.
            db.ChangeTracker.Clear();
            logger.LogDebug("Email for event {EventId} was already queued; skipping the duplicate",
                domainEvent.EventId);
        }
    }
}
