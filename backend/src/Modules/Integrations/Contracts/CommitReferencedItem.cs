using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Integrations.Contracts;

/// <summary>One commit/reference pair discovered in a GitHub push.</summary>
public sealed record CommitReferencedItem(
    Guid OrganizationId, Guid ProjectId, int ItemNumber, string Sha, string Message,
    string AuthorName, string? AuthorLogin, string? AuthorEmail, string Url, string Branch)
    : DomainEvent, IIntegrationEvent;

/// <summary>One work-item reference discovered in a GitHub pull request.</summary>
public sealed record PullRequestReferencedItem(
    Guid OrganizationId, Guid ProjectId, int ItemNumber, long PullRequestId, int Number,
    string Title, string Url, string State, string? AuthorLogin, IReadOnlyList<string> Reviewers,
    string HeadBranch, bool IsOpened, bool IsMerged, bool HasClosingKeyword,
    Guid? OpenedTransitionStateId, Guid? MergedTransitionStateId)
    : DomainEvent, IIntegrationEvent;
