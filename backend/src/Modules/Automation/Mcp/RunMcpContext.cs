using System.Text;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Tenancy;
using ModelContextProtocol.Server;

namespace Aictiq.Modules.Automation.Mcp;

/// <summary>The run as a document, for clients that read resources rather than call tools.</summary>
[McpServerResourceType]
public sealed class RunMcpContext(
    AutomationDbContext db, ICurrentUser user, ICurrentTenant tenant, IProjectAccess access, IUserDirectory directory)
{
    [McpServerResource(UriTemplate = "aictiq://run/{id}", Name = "run", MimeType = "text/markdown")]
    public async Task<string> Run(string id, CancellationToken cancellationToken)
    {
        var visible = Guid.TryParse(id, out var runId)
            ? await RunMcpViews.FindAsync(db, access, user, tenant, runId, cancellationToken)
            : null;
        if (visible is null) return "# Run not found";

        var run = visible.Run;
        var names = await RunMcpViews.NamesAsync(db, directory, run, cancellationToken);
        var text = new StringBuilder();
        text.AppendLine($"# Run {run.Id} - {run.ItemKey} ({RunMcpViews.StatusName(run.Status)})");
        text.AppendLine();
        text.AppendLine($"- Agent: {names.Agent ?? "(unknown)"} ({run.AgentUserId})");
        text.AppendLine($"- Playbook: {names.Playbook ?? "(deleted)"}, harness {run.Harness}, at most {run.MaxMinutes} minutes");
        text.AppendLine($"- Requested by: {names.RequestedBy ?? run.RequestedBy}");
        text.AppendLine($"- Runner: {names.Runner ?? "(none yet)"}");
        text.AppendLine($"- Queued: {run.QueuedAt:O}");
        if (run.StartedAt is { } started) text.AppendLine($"- Started: {started:O}");
        if (run.FinishedAt is { } finished) text.AppendLine($"- Finished: {finished:O}");
        if (run.CancelRequestedAt is not null) text.AppendLine("- Cancel requested: yes");
        if (run.PullRequestUrl is { } pullRequest) text.AppendLine($"- Pull request: {pullRequest}");
        if (run.ExitCode is { } exitCode) text.AppendLine($"- Exit code: {exitCode}");
        if (visible.IsOperator && run.FailureReason is { } failure) text.AppendLine($"- Failure reason: {failure}");

        if (run.OutcomeSummary is { } summary)
        {
            text.AppendLine();
            text.AppendLine("## Outcome");
            text.AppendLine();
            text.AppendLine(McpContentBoundary.Wrap(summary));
        }

        // The raw agent output is the operators' to read, as on the REST log route.
        if (visible.IsOperator)
        {
            var lines = await RunMcpViews.LogTailAsync(db, run.Id, cancellationToken);
            text.AppendLine();
            text.AppendLine($"## Log (last {RunMcpViews.LogTail} lines)");
            text.AppendLine();
            text.AppendLine(McpContentBoundary.Wrap(lines.Count == 0
                ? "(no output yet)"
                : string.Join("\n", lines.Select(FormatLine))));
        }

        return text.ToString();
    }

    // A chunk usually ends in its own newline and may hold several lines; as in
    // `aictiq run logs`, each line of a stderr or event chunk carries the prefix.
    private static string FormatLine(RunMcpViews.LogLine line)
    {
        var body = line.Text.EndsWith('\n') ? line.Text[..^1] : line.Text;
        return line.Stream == "stdout"
            ? body
            : string.Join("\n", body.Split('\n').Select(part => $"[{line.Stream}] {part}"));
    }
}
