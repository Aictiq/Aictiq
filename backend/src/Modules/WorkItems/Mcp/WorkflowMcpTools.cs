using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Tenancy.Access;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.WorkItems.Mcp;

public sealed record McpWorkflowStateView(Guid Id, string Name, WorkflowStateCategory Category, int Position, bool IsInitial);
public sealed record McpWorkflowTransitionView(Guid? FromStateId, Guid ToStateId);
public sealed record McpWorkflowView(Guid Id, string Name, bool IsDefault, uint Version,
    IReadOnlyList<McpWorkflowStateView> States, IReadOnlyList<McpWorkflowTransitionView> Transitions);

/// <summary>Workflow context for tools that need to select valid item transitions.</summary>
[McpServerToolType]
public sealed class WorkflowMcpTools(
    WorkItemsDbContext db,
    TenancyDbContext tenancy,
    ICurrentTenant tenant,
    ICurrentUser user,
    IProjectAccess access)
{
    [McpServerTool(Name = "get_workflow", ReadOnly = true)]
    [Description("Gets the workflows and valid state transitions for a project key, such as ACME.")]
    public async Task<IReadOnlyList<McpWorkflowView>> GetWorkflow(
        [Description("The project key, for example ACME.")] string projectKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || tenant.OrganizationId is not { } organizationId)
        {
            return [];
        }

        var organizationRole = await access.GetOrgRoleAsync(user.UserId!, organizationId, cancellationToken);
        if (organizationRole is null)
        {
            return [];
        }

        var project = await tenancy.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == projectKey.Trim().ToUpperInvariant(), cancellationToken);
        if (project is null)
        {
            return [];
        }

        var explicitRole = await tenancy.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == project.Id && m.UserId == user.UserId)
            .Select(m => (ProjectRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (ProjectAccessRules.Effective(organizationRole, project.Visibility, explicitRole) is null)
        {
            return [];
        }

        var workflows = await db.Workflows.AsNoTracking()
            .Where(w => w.ProjectId == project.Id)
            .OrderByDescending(w => w.IsDefault)
            .ToListAsync(cancellationToken);
        var ids = workflows.Select(w => w.Id).ToArray();
        var states = await db.WorkflowStates.AsNoTracking()
            .Where(s => ids.Contains(s.WorkflowId))
            .OrderBy(s => s.Position)
            .ToListAsync(cancellationToken);
        var transitions = await db.WorkflowTransitions.AsNoTracking()
            .Where(t => ids.Contains(t.WorkflowId))
            .ToListAsync(cancellationToken);

        return workflows.Select(workflow => new McpWorkflowView(
            workflow.Id, workflow.Name, workflow.IsDefault, workflow.Version,
            states.Where(s => s.WorkflowId == workflow.Id)
                .Select(s => new McpWorkflowStateView(s.Id, s.Name, s.Category, s.Position, s.IsInitial)).ToList(),
            transitions.Where(t => t.WorkflowId == workflow.Id)
                .Select(t => new McpWorkflowTransitionView(t.FromStateId, t.ToStateId)).ToList())).ToList();
    }
}

[McpServerResourceType]
public sealed class WorkflowMcpContext(WorkItemsDbContext db, TenancyDbContext tenancy, ICurrentTenant tenant,
    ICurrentUser user, IProjectAccess access)
{
    [McpServerResource(UriTemplate = "aictiq://project/{key}/workflow", Name = "project-workflow", MimeType = "application/json")]
    public async Task<string> Workflow(string key, CancellationToken cancellationToken) =>
        // A resource answers with text: the SDK has no conversion for an arbitrary object, so
        // returning one failed every read of this resource.
        JsonSerializer.Serialize(await WorkflowAsync(key, cancellationToken), Json);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private async Task<object> WorkflowAsync(string key, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId is not { } organizationId) return new { error = "Project not found" };
        var project = await tenancy.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key.Trim().ToUpperInvariant() && x.OrganizationId == organizationId, cancellationToken);
        if (project is null || await access.GetProjectRoleAsync(user.UserId!, project.Id, cancellationToken) is null) return new { error = "Project not found" };
        var workflows = await db.Workflows.AsNoTracking().Where(x => x.ProjectId == project.Id).OrderByDescending(x => x.IsDefault).ToListAsync(cancellationToken);
        var ids = workflows.Select(x => x.Id).ToArray();
        var states = await db.WorkflowStates.AsNoTracking().Where(x => ids.Contains(x.WorkflowId)).OrderBy(x => x.Position).ToListAsync(cancellationToken);
        var transitions = await db.WorkflowTransitions.AsNoTracking().Where(x => ids.Contains(x.WorkflowId)).ToListAsync(cancellationToken);
        return new { project = project.Key, workflows = workflows.Select(workflow => new McpWorkflowView(workflow.Id, workflow.Name, workflow.IsDefault, workflow.Version,
            states.Where(x => x.WorkflowId == workflow.Id).Select(x => new McpWorkflowStateView(x.Id, x.Name, x.Category, x.Position, x.IsInitial)).ToList(),
            transitions.Where(x => x.WorkflowId == workflow.Id).Select(x => new McpWorkflowTransitionView(x.FromStateId, x.ToStateId)).ToList())) };
    }
}
