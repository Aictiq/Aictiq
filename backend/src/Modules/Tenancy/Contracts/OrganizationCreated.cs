using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Contracts;

/// <summary>
/// Integration event: written to shared.outbox_messages in the same transaction as the
/// organization and its first membership, so nothing downstream can observe an
/// organization that does not exist (or miss one that does).
///
/// Later phases hang work off this — a welcome email, a default project
///, analytics. Every one of those handlers must be idempotent: outbox delivery
/// is at least once.
/// </summary>
public sealed record OrganizationCreated(Guid OrganizationId, string Slug, string Name, string CreatedBy)
    : DomainEvent, IIntegrationEvent;
