using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Billing.Events;

/// <summary>
/// Mints the evaluation a new hosted organization is born with: thirty days of
/// the Hosted entitlement, from the same <c>OrganizationCreated</c> event the outbox
/// already carries past every module.
///
/// Idempotent the way the repo prefers: not by checking first, but by letting the unique
/// index on the organization decide. A redelivered event, a Workers restart, or two
/// deliveries racing all produce one insert and one refusal — the refusal is caught and
/// read as success, exactly like a webhook's <c>ON CONFLICT DO NOTHING</c> ledger.
/// Self-hosted instances answer nothing: no billing, no evaluation, no clock.
/// </summary>
public sealed class EvaluationStartHandler(
    BillingDbContext db, AmbientCurrentTenant tenant, IOptions<BillingOptions> options,
    TimeProvider clock, ILogger<EvaluationStartHandler> logger)
    : IDomainEventHandler<OrganizationCreated>
{
    public async Task HandleAsync(OrganizationCreated domainEvent, CancellationToken cancellationToken)
    {
        if (!options.Value.IsSaas) return;

        using var scope = tenant.Use(domainEvent.OrganizationId);
        if (await db.Evaluations.AnyAsync(cancellationToken)) return;

        var now = clock.GetUtcNow();
        db.Evaluations.Add(new Evaluation
        {
            OrganizationId = domainEvent.OrganizationId,
            StartedAt = now,
            EndsAt = now.AddDays(options.Value.EvaluationDays),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Started the {Days}-day evaluation for organization {OrganizationId}",
                options.Value.EvaluationDays, domainEvent.OrganizationId);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Another delivery of this event — or a racing one — won the race to insert.
            // The window exists, which is everything this handler promised.
            db.ChangeTracker.Clear();
        }
    }

    // One class answering one event still has to say which default bridge the dispatcher's
    // untyped call goes through.
    Task IDomainEventHandler.HandleAsync(IDomainEvent domainEvent, CancellationToken cancellationToken) =>
        domainEvent is OrganizationCreated created ? HandleAsync(created, cancellationToken) : Task.CompletedTask;
}
