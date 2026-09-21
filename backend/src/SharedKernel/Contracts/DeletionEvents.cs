using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Contracts;

// Hard deletion is owned by the module that owns the deleted row and finished by every module
// that keeps something beside it. The owner deletes its own rows in the request's transaction
// and writes one of these to the outbox in that same transaction; each other module removes
// what it holds. They live in SharedKernel because the modules that react may not reference
// the module that raised them. Deleting what is already gone is success, so every handler is
// idempotent by construction — a replay finds nothing left to delete.

/// <summary>
/// Work items were deleted for good, each with every item below it. WorkItems has already
/// removed its own rows; <paramref name="ObjectKeys"/> are the attachment objects those rows
/// named, because the rows that knew them are gone.
/// </summary>
public sealed record WorkItemsDeleted(
    Guid OrganizationId, Guid ProjectId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<string> ObjectKeys, string ActorId)
    : DomainEvent, IIntegrationEvent;

/// <summary>
/// A project was deleted for good. Tenancy has removed the project, its memberships and its
/// teams; <paramref name="TeamIds"/> are the teams it had, for the rows keyed by team rather
/// than by project (sprints, boards).
/// </summary>
public sealed record ProjectDeleted(
    Guid OrganizationId, Guid ProjectId, string Key, IReadOnlyList<Guid> TeamIds, string ActorId)
    : DomainEvent, IIntegrationEvent;

/// <summary>
/// Sprints no longer exist because their project was deleted. Raised by WorkItems while it
/// purges the project, for Analytics, whose scope log is keyed by sprint and cannot ask which
/// project a sprint belonged to once WorkItems has forgotten it.
/// </summary>
public sealed record SprintsDeleted(Guid OrganizationId, Guid ProjectId, IReadOnlyList<Guid> SprintIds)
    : DomainEvent, IIntegrationEvent;

/// <summary>
/// An organization was deleted for good, with everything in it. Tenancy has removed its own
/// rows; every module deletes what it keeps under <paramref name="OrganizationId"/>.
/// <paramref name="AgentIds"/> are the agent accounts that belonged to nowhere else — an agent
/// is created inside one organization, and without it the account is answerable to nobody.
/// </summary>
public sealed record OrganizationDeleted(
    Guid OrganizationId, string Slug, IReadOnlyList<string> AgentIds, string ActorId)
    : DomainEvent, IIntegrationEvent;

/// <summary>Where the rows of a deleted record's audit trail live, by entity type.</summary>
public static class AuditEntityTypes
{
    public const string WorkItem = "WorkItem";
    public const string WikiPage = "WikiPage";
    public const string Project = "Project";
    public const string ProjectMember = "ProjectMember";
    public const string Team = "Team";
    public const string TeamMember = "TeamMember";
    public const string WebhookSubscription = "WebhookSubscription";
    public const string RepoBinding = "RepoBinding";
    public const string Playbook = "Playbook";
    public const string ProjectFactorySettings = "ProjectFactorySettings";
    public const string Rule = "Rule";
}
