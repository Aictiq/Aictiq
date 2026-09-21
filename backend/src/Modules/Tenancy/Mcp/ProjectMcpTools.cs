using System.ComponentModel;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.Tenancy.Mcp;

public sealed record McpProjectView(
    Guid Id, string Key, string Name, string? Description, ProjectVisibility Visibility,
    bool IsArchived, ProjectRole Role, IReadOnlyList<McpProjectLabel> Labels,
    IReadOnlyList<McpProjectTemplate> Templates);

/// <summary>Read-only project context for MCP clients bound to one organization.</summary>
[McpServerToolType]
public sealed class ProjectMcpTools(
    TenancyDbContext db,
    ICurrentTenant tenant,
    ICurrentUser user,
    IProjectAccess access,
    IProjectMcpDetails planning)
{
    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("Lists projects visible to the current agent in its bound organization.")]
    public async Task<IReadOnlyList<McpProjectView>> ListProjects(CancellationToken cancellationToken)
    {
        var organizationId = tenant.OrganizationId
            ?? throw new InvalidOperationException("An MCP token must be bound to an organization.");
        var organizationRole = await access.GetOrgRoleAsync(user.UserId!, organizationId, cancellationToken);
        if (organizationRole is null)
        {
            return [];
        }

        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.ArchivedAt == null)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        var explicitRoles = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == user.UserId)
            .ToDictionaryAsync(m => m.ProjectId, m => m.Role, cancellationToken);

        var visible = projects
            .Select(p => (project: p, role: ProjectAccessRules.Effective(
                organizationRole, p.Visibility,
                explicitRoles.TryGetValue(p.Id, out var explicitRole) ? explicitRole : null)))
            .Where(x => x.role is not null)
            .Select(x => (x.project, role: x.role!.Value)).ToList();
        // One project at a time: the planning details come from a scoped DbContext, which
        // refuses concurrent queries — Task.WhenAll here failed every call with more than one
        // visible project.
        var views = new List<McpProjectView>(visible.Count);
        foreach (var (project, role) in visible)
        {
            views.Add(ToView(project, role, await planning.GetAsync(project.Id, cancellationToken)));
        }
        return views;
    }

    [McpServerTool(Name = "get_project", ReadOnly = true)]
    [Description("Gets one visible project by its short uppercase key, such as ACME.")]
    public async Task<McpProjectView?> GetProject(
        [Description("The project key, for example ACME.")] string projectKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            throw new McpAnswerException("project not found or no access");
        }

        var organizationId = tenant.OrganizationId
            ?? throw new InvalidOperationException("An MCP token must be bound to an organization.");
        var organizationRole = await access.GetOrgRoleAsync(user.UserId!, organizationId, cancellationToken);
        if (organizationRole is null)
        {
            throw new McpAnswerException("project not found or no access");
        }

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == projectKey.Trim().ToUpperInvariant(), cancellationToken);
        if (project is null)
        {
            throw new McpAnswerException($"project '{projectKey.Trim().ToUpperInvariant()}' not found or no access");
        }

        var explicitRole = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == project.Id && m.UserId == user.UserId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        var role = ProjectAccessRules.Effective(organizationRole, project.Visibility, explicitRole);
        return role is null
            ? throw new McpAnswerException($"project '{project.Key}' not found or no access")
            : ToView(project, role.Value, await planning.GetAsync(project.Id, cancellationToken));
    }

    private static McpProjectView ToView(Project project, ProjectRole role, McpProjectPlanningDetails planning) => new(
        project.Id, project.Key, project.Name, project.Description, project.Visibility,
        project.ArchivedAt is not null, role, planning.Labels, planning.Templates);
}
