namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// What a project-chat run's token may create: items in <paramref name="ProjectId"/>, reported
/// by <paramref name="RequestedBy"/>, the person whose question the run answers.
/// </summary>
public sealed record ProjectChatGrant(Guid ProjectId, string RequestedBy);

/// <summary>
/// Resolves the grant behind a token with the <see cref="Authorization.Scopes.ProjectChat"/>
/// scope. Implemented by Automation, which owns runs; asked by WorkItems before it creates an
/// item for such a token.
/// </summary>
public interface IProjectChatGrants
{
    /// <summary>
    /// The grant of the live project-chat run that holds <paramref name="tokenId"/> as its agent
    /// token, or null when no live run does. Fails closed: a finished run grants nothing.
    /// </summary>
    Task<ProjectChatGrant?> FindAsync(Guid tokenId, CancellationToken cancellationToken = default);
}
