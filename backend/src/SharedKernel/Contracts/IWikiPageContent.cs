namespace Aictiq.SharedKernel.Contracts;

/// <summary>The current Markdown of a wiki page, exposed without leaking Wiki's tables.</summary>
public interface IWikiPageContent
{
    Task<WikiPageContent?> GetMarkdownAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default);
}

public sealed record WikiPageContent(string Title, string Markdown, Guid RevisionId);

/// <summary>
/// Writes the pages playbooks run from. They live in the project's Factory section of the
/// wiki, apart from the pages people write for each other, so a playbook is made and edited
/// from the Factory screen and never borrows a page such as Home.
/// </summary>
public interface IWikiPageCreator
{
    /// <summary>Factory/Implement for the starter playbook. Null when it already exists.</summary>
    Task<Guid?> CreateStarterPageAsync(
        Guid organizationId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default);

    /// <summary>A new page in the Factory section, which is created first when the project has none.</summary>
    Task<Guid?> CreateFactoryPageAsync(
        Guid organizationId, Guid projectId, string actorId, string title, string markdown,
        CancellationToken cancellationToken = default);

    /// <summary>A new revision of a Factory section page. False when the page is gone or outside the section.</summary>
    Task<bool> WriteFactoryPageAsync(
        Guid pageId, Guid projectId, string actorId, string markdown,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the page is below the project's Factory section.</summary>
    Task<bool> IsInFactorySectionAsync(
        Guid pageId, Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The wiki pages the AI factory runs from, answered by Automation. The Wiki module hides
/// them from anyone who may not operate the factory: a playbook is how the factory works,
/// and a stakeholder sees the work, not the machinery.
/// </summary>
public interface IFactoryPages
{
    /// <summary>Pages a playbook in this project points at.</summary>
    Task<IReadOnlyList<Guid>> ListPlaybookPageIdsAsync(Guid projectId, CancellationToken cancellationToken = default);
}
