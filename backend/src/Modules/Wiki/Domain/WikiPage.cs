using Aictiq.Modules.Wiki.Contracts;
using Aictiq.SharedKernel.Domain;
using NpgsqlTypes;

namespace Aictiq.Modules.Wiki.Domain;

public sealed class WikiPage : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public Guid? ParentId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public int Position { get; set; }
    public Guid? CurrentRevisionId { get; set; }
    /// <summary>
    /// The root of the project's Factory section, where every playbook's page lives. A flag
    /// rather than a slug, so renaming or moving the section cannot turn it into an
    /// ordinary page; at most one per project.
    /// </summary>
    public bool IsFactorySection { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public uint Version { get; private set; }
    public NpgsqlTsVector Search { get; private set; } = null!;

    public void Updated(string actorId, string change) =>
        Raise(new WikiPageUpdated(OrganizationId, ProjectId, Id, actorId, change));

    public void LinkedItems(Guid revisionId, IReadOnlyList<Guid> itemIds) =>
        Raise(new WikiPageLinkedItems(OrganizationId, ProjectId, Id, revisionId, itemIds));
}

public sealed class WikiPageRevision : TenantEntity
{
    public Guid PageId { get; init; }
    public int Number { get; init; }
    public required string ContentMarkdown { get; init; }
    public required string ContentHtml { get; init; }
    public required string AuthorId { get; init; }
    public DateTimeOffset At { get; init; }
    public string? Summary { get; init; }
}

/// <summary>Current work-item references for a page; replacement is keyed by page and item.</summary>
public sealed class WikiPageItemLink : TenantEntity
{
    public Guid PageId { get; init; }
    public Guid ItemId { get; init; }
    public Guid RevisionId { get; init; }
}

public enum WikiPermissionSubjectKind : short
{
    Team = 0,
    User = 1,
    ProjectRole = 2,
}

public enum WikiPermissionAccess : short
{
    Read = 0,
    Write = 1,
}

/// <summary>An explicit rule set replaces inherited access for this page's subtree.</summary>
public sealed class WikiPagePermission : TenantEntity
{
    public Guid PageId { get; init; }
    public WikiPermissionSubjectKind SubjectKind { get; init; }
    /// <summary>User id, team id, or the lower-case project role name.</summary>
    public required string SubjectId { get; init; }
    public WikiPermissionAccess Access { get; init; }
}
