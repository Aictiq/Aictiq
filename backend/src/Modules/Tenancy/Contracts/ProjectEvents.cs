using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Tenancy.Contracts;

/// <summary>
/// A new workspace exists. Wiki and WorkItems create their per-project defaults off this;
/// every handler must be idempotent, because outbox delivery is at least once.
///
/// The project's own default team is <em>not</em> created here — Tenancy owns both tables
/// in one schema, so it writes them in one transaction and there is no window in which a
/// project has no default team.
/// </summary>
public sealed record ProjectCreated(
    Guid OrganizationId, Guid ProjectId, string Key, string Name, string CreatedBy)
    : DomainEvent, IIntegrationEvent;

/// <summary>The project went read-only. Modules that own rows under it mark them so too.</summary>
public sealed record ProjectArchived(
    Guid OrganizationId, Guid ProjectId, string Key, string ArchivedBy)
    : DomainEvent, IIntegrationEvent;

/// <inheritdoc cref="ProjectArchived"/>
public sealed record ProjectUnarchived(
    Guid OrganizationId, Guid ProjectId, string Key, string UnarchivedBy)
    : DomainEvent, IIntegrationEvent;

/// <param name="Role">Null when the membership was removed.</param>
/// <param name="PreviousRole">Null when it was just created.</param>
public sealed record ProjectMembershipChanged(
    Guid OrganizationId, Guid ProjectId, string UserId,
    ProjectRole? Role, ProjectRole? PreviousRole, string ChangedBy)
    : DomainEvent, IIntegrationEvent;
