using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

public sealed record WorkflowStateView(Guid Id, string Name, WorkflowStateCategory Category, int Position, string? Color, bool IsInitial);
public sealed record WorkflowView(Guid Id, string Name, bool IsDefault, uint Version, IReadOnlyList<WorkflowStateView> States, IReadOnlyList<WorkflowTransitionView> Transitions);
public sealed record WorkflowTransitionView(Guid? FromStateId, Guid ToStateId);
public sealed record WorkflowStateInput(Guid? Id, string? Name, WorkflowStateCategory Category, int Position, string? Color, bool IsInitial, Guid? ReplacementStateId);
public sealed record WorkflowTransitionInput(Guid? FromStateId, Guid ToStateId);
public sealed record ReplaceWorkflowRequest(string? Name, IReadOnlyList<WorkflowStateInput>? States, IReadOnlyList<WorkflowTransitionInput>? Transitions, uint Version);
public sealed record ReorderStatesRequest(IReadOnlyList<Guid>? StateIds, uint Version);

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/orgs/{orgSlug}/projects/{projectKey}/workflows").WithTags("Workflows").RequireAuthorization();
        group.MapGet("/", async (HttpContext http, WorkItemsDbContext db, ICurrentTenant tenant, CancellationToken ct) =>
        {
            var projectId = http.ResolvedProjectId()!.Value;
            // The durable ProjectCreated handler runs through the outbox. Ensure the
            // default here too so a just-created project is immediately usable before
            // the worker's first sweep; the filtered unique index makes the replay safe.
            await EnsureDefaultAsync(db, tenant.OrganizationId!.Value, projectId, ct);
            var workflows = await db.Workflows.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.IsDefault).ToListAsync(ct);
            var ids = workflows.Select(x => x.Id).ToArray();
            var states = await db.WorkflowStates.AsNoTracking().Where(x => ids.Contains(x.WorkflowId)).OrderBy(x => x.Position).ToListAsync(ct);
            var transitions = await db.WorkflowTransitions.AsNoTracking().Where(x => ids.Contains(x.WorkflowId)).ToListAsync(ct);
            return Results.Ok(workflows.Select(w => ToView(w, states, transitions)).ToList());
        }).RequireProjectRole(ProjectRole.Guest).RequireScope(Scopes.Read);
        group.MapPut("/{workflowId:guid}", Replace).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        group.MapPost("/{workflowId:guid}/states/reorder", Reorder).RequireProjectRole(ProjectRole.Admin).RequireProjectWritable().RequireScope(Scopes.Write);
        return api;
    }
    private static async Task<IResult> Replace(Guid workflowId, ReplaceWorkflowRequest request, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var workflow = await db.Workflows.Include(x => x.States).FirstOrDefaultAsync(x => x.Id == workflowId && x.ProjectId == http.ResolvedProjectId(), ct);
        if (workflow is null) return Results.NotFound();
        if (workflow.Version != request.Version) return Conflict();
        var errors = Validate(request.States, request.Transitions, workflow.States);
        if (errors.Count > 0) return Results.ValidationProblem(errors, type: ProblemTypes.Validation);
        var incoming = request.States!; var oldById = workflow.States.ToDictionary(x => x.Id);
        var deleted = workflow.States.Where(x => !incoming.Any(i => i.Id == x.Id)).ToList();
        foreach (var old in deleted)
        {
            var replacement = incoming.FirstOrDefault(x => x.ReplacementStateId == old.Id)?.Id;
            var inUse = await db.Items.AnyAsync(x => x.StateId == old.Id, ct);
            if (inUse && replacement is not { } replacementId) return Results.Problem("State is in use.", "Choose a replacement state before deleting it.", statusCode: StatusCodes.Status409Conflict);
            if (inUse) await db.Items.Where(x => x.StateId == old.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StateId, replacement!.Value), ct);
            db.WorkflowStates.Remove(old);
        }
        foreach (var input in incoming)
        {
            if (input.Id is { } id && oldById.TryGetValue(id, out var state)) { state.Name = input.Name!.Trim(); state.Category = input.Category; state.Position = input.Position; state.Color = input.Color; state.IsInitial = input.IsInitial; }
            else db.WorkflowStates.Add(new WorkflowState { WorkflowId = workflow.Id, Name = input.Name!.Trim(), Category = input.Category, Position = input.Position, Color = input.Color, IsInitial = input.IsInitial });
        }
        workflow.Name = request.Name?.Trim() is { Length: > 0 } name ? name : workflow.Name;
        await db.SaveChangesAsync(ct);
        // State IDs are allocated by EF for new states, so transitions are intentionally replaced after this flush.
        db.WorkflowTransitions.RemoveRange(db.WorkflowTransitions.Where(x => x.WorkflowId == workflow.Id));
        foreach (var transition in request.Transitions!) db.WorkflowTransitions.Add(new WorkflowTransition { WorkflowId = workflow.Id, FromStateId = transition.FromStateId, ToStateId = transition.ToStateId });
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(workflow, await db.WorkflowStates.Where(x => x.WorkflowId == workflow.Id).OrderBy(x => x.Position).ToListAsync(ct), await db.WorkflowTransitions.Where(x => x.WorkflowId == workflow.Id).ToListAsync(ct)));
    }
    private static async Task<IResult> Reorder(Guid workflowId, ReorderStatesRequest request, HttpContext http, WorkItemsDbContext db, CancellationToken ct)
    {
        var workflow = await db.Workflows.Include(x => x.States).FirstOrDefaultAsync(x => x.Id == workflowId && x.ProjectId == http.ResolvedProjectId(), ct);
        if (workflow is null) return Results.NotFound(); if (workflow.Version != request.Version) return Conflict();
        if (request.StateIds is null || request.StateIds.Count != workflow.States.Count || request.StateIds.Distinct().Count() != workflow.States.Count || request.StateIds.Except(workflow.States.Select(x => x.Id)).Any()) return Results.ValidationProblem(new Dictionary<string, string[]> { ["stateIds"] = ["Supply every state exactly once."] });
        for (var i = 0; i < request.StateIds.Count; i++) workflow.States.Single(x => x.Id == request.StateIds[i]).Position = i;
        await db.SaveChangesAsync(ct); return Results.Ok();
    }
    /// <summary>Creates the standard workflow for a newly provisioned project when absent.</summary>
    public static async Task EnsureDefaultAsync(WorkItemsDbContext db, Guid organizationId, Guid projectId, CancellationToken ct)
    {
        if (await db.Workflows.AnyAsync(x => x.ProjectId == projectId && x.IsDefault, ct)) return;
        var workflow = new Workflow { OrganizationId = organizationId, ProjectId = projectId, Name = "Default", IsDefault = true };
        db.Workflows.Add(workflow);
        db.WorkflowStates.AddRange(
            new WorkflowState { WorkflowId = workflow.Id, Name = "New", Category = WorkflowStateCategory.Proposed, Position = 0, IsInitial = true },
            new WorkflowState { WorkflowId = workflow.Id, Name = "Active", Category = WorkflowStateCategory.Active, Position = 1 },
            new WorkflowState { WorkflowId = workflow.Id, Name = "In Review", Category = WorkflowStateCategory.Active, Position = 2 },
            new WorkflowState { WorkflowId = workflow.Id, Name = "Resolved", Category = WorkflowStateCategory.Resolved, Position = 3 },
            new WorkflowState { WorkflowId = workflow.Id, Name = "Closed", Category = WorkflowStateCategory.Completed, Position = 4 },
            new WorkflowState { WorkflowId = workflow.Id, Name = "Removed", Category = WorkflowStateCategory.Removed, Position = 5 });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request (or the outbox worker) won the unique default-workflow
            // race. Detach this attempt and let the caller read the winner.
            foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added).ToList()) entry.State = EntityState.Detached;
        }
    }
    private static Dictionary<string, string[]> Validate(IReadOnlyList<WorkflowStateInput>? states, IReadOnlyList<WorkflowTransitionInput>? transitions, ICollection<WorkflowState> current)
    {
        var errors = new Dictionary<string, string[]>(); if (states is null || states.Count == 0) { errors["states"] = ["At least one state is required."]; return errors; }
        if (states.Count(x => x.IsInitial) != 1) errors["states"] = ["Exactly one initial state is required."];
        foreach (var category in new[] { WorkflowStateCategory.Proposed, WorkflowStateCategory.Active, WorkflowStateCategory.Completed }) if (!states.Any(x => x.Category == category)) errors["states"] = ["Proposed, Active, and Completed states are required."];
        if (states.Any(x => string.IsNullOrWhiteSpace(x.Name)) || states.GroupBy(x => x.Name?.Trim(), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) errors["states"] = ["State names must be present and unique."];
        // An id on an incoming state has to be one of *this* workflow's states. A state id
        // from another workflow — another project's, or another organization's — is not a
        // retained state, and a transition or replacement pointing at it would move items
        // into a state their own workflow cannot see.
        var known = current.Select(x => x.Id).ToHashSet();
        var ids = states.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToHashSet();
        if (!ids.IsSubsetOf(known)) errors["states"] = ["Unknown state id. Leave id empty for a new state."];
        // ReplacementStateId names the state being *deleted* that this one takes over, so it
        // has to be one of this workflow's current states — not an id from somewhere else.
        if (states.Any(x => x.ReplacementStateId is { } replacement && !known.Contains(replacement))) errors["states"] = ["A replacement must name one of this workflow's own states."];
        if (transitions?.Any(x => (x.FromStateId.HasValue && !ids.Contains(x.FromStateId.Value)) || !ids.Contains(x.ToStateId)) == true) errors["transitions"] = ["Transitions must reference retained states. Add new-state transitions in a following update."];
        return errors;
    }
    private static WorkflowView ToView(Workflow w, IEnumerable<WorkflowState> states, IEnumerable<WorkflowTransition> transitions) => new(w.Id, w.Name, w.IsDefault, w.Version, states.Where(x => x.WorkflowId == w.Id).OrderBy(x => x.Position).Select(x => new WorkflowStateView(x.Id, x.Name, x.Category, x.Position, x.Color, x.IsInitial)).ToList(), transitions.Where(x => x.WorkflowId == w.Id).Select(x => new WorkflowTransitionView(x.FromStateId, x.ToStateId)).ToList());
    private static IResult Conflict() => Results.Problem("The workflow was modified by someone else.", statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.Conflict);
}
