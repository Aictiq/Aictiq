using System.ComponentModel;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using ModelContextProtocol.Server;

namespace Aictiq.Api.Mcp;

/// <param name="CanOperateFactory">
/// Whether this identity may start AI runs in its organization. An agent whose
/// flag an administrator cleared can still work items; it may not delegate to other agents.
/// </param>
public sealed record McpWhoAmI(
    string UserId, string? Name, bool IsAgent, Guid? OrganizationId,
    IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Scopes, bool CanOperateFactory);

/// <summary>Identity context that every MCP client should inspect before taking action.</summary>
[McpServerToolType]
public sealed class McpContextTools(ICurrentUser currentUser, IProjectAccess access)
{
    [McpServerTool(Name = "whoami", ReadOnly = true)]
    [Description("Returns the authenticated Aictiq identity, organization binding, roles, token scopes, and whether it may start AI runs (canOperateFactory).")]
    public async Task<McpWhoAmI> WhoAmI(CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId ?? throw new InvalidOperationException("Not authenticated.");
        var operates = currentUser.OrganizationId is { } organizationId
            && await access.CanOperateFactoryAsync(userId, organizationId, cancellationToken);

        return new McpWhoAmI(
            userId,
            currentUser.UserName,
            currentUser.IsAgent,
            currentUser.OrganizationId,
            currentUser.Roles,
            currentUser.Scopes,
            operates);
    }
}
