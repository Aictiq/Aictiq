using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.WorkItems.Mcp;

[McpServerResourceType]
public sealed class WorkItemMcpContext(WorkItemsDbContext db, ICurrentUser user, IProjectAccess access)
{
    [McpServerResource(UriTemplate = "aictiq://item/{key}", Name = "item", MimeType = "text/markdown")]
    public async Task<string> Item(string key, CancellationToken cancellationToken)
    {
        var item = await WorkItemEndpoints.FindVisible(db, access, user, key, cancellationToken);
        if (item is null) return "# Item not found";
        var parents = new List<string>();
        for (var id = item.ParentId; id is { } parentId && parents.Count < 10;)
        { var parent = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == parentId, cancellationToken); if (parent is null) break; parents.Add(parent.Key); id = parent.ParentId; }
        return McpContentBoundary.Wrap($"# {item.Key}: {item.Title}\n\n{item.DescriptionMarkdown}\n\nClaimed by: {item.ClaimedBy ?? "nobody"}\n\nParent chain: {(parents.Count == 0 ? "none" : string.Join(" → ", parents))}");
    }
}

[McpServerPromptType]
public sealed class WorkItemMcpPrompts
{
    [McpServerPrompt(Name = "work-on-item")]
    public string WorkOnItem(string key) => $"""
        Work on {key}. First call get_item and read its parent chain and linked context.
        Claim it with its current version before making changes. Keep the heartbeat alive while
        working, add a concise progress comment, link the resulting PR, then transition it only
        through an allowed workflow state. Release the claim if you cannot continue.
        A subtask you would rather hand to another agent can be delegated with start_run(subtaskKey)
        and watched with get_run(runId) — if whoami says canOperateFactory; the run records you as the requester.
        """;

    [McpServerPrompt(Name = "triage-bug")]
    public string TriageBug(string key) => $"""
        Triage bug {key}. Read the item, its comments, links, parent chain, and any linked wiki context.
        Reproduce or narrow the report, identify impact and likely ownership, then add a concise comment with
        observed behaviour, expected behaviour, evidence, and a recommended next state. Do not transition the
        item unless the workflow explicitly permits the conclusion and the current version is still current.
        """;
}
