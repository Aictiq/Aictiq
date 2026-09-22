namespace Aictiq.SharedKernel.Contracts;

/// <summary>The current Markdown of a wiki page, exposed without leaking Wiki's tables.</summary>
public interface IWikiPageContent
{
    Task<WikiPageContent?> GetMarkdownAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default);
}

public sealed record WikiPageContent(string Title, string Markdown, Guid RevisionId);

/// <summary>Creates the built-in page used by Factory's starter playbook.</summary>
public interface IWikiPageCreator
{
    Task<Guid?> CreateStarterPageAsync(
        Guid organizationId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default);
}
