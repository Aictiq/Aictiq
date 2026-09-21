using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aictiq.Modules.Identity;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Api.Mcp;

/// <summary>Writes one append-only row and one metric for every MCP tool invocation.</summary>
public sealed class McpToolAuditService(IdentityDbContext db, ICurrentUser user, TimeProvider clock)
{
    private static readonly Meter Meter = new("Aictiq.Mcp");
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("mcp.tool.calls");

    public async Task RecordAsync(string toolName, string outcome, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        Calls.Add(1, new KeyValuePair<string, object?>("tool", toolName),
            new KeyValuePair<string, object?>("outcome", outcome));

        // AuditLogEntry is intentionally generic and append-only. Keeping the tool name
        // in EntityId makes a tool's history searchable with the existing audit queries.
        db.Set<AuditLogEntry>().Add(new AuditLogEntry
        {
            EntityType = "mcp.tool",
            EntityId = toolName.Length <= 128 ? toolName : toolName[..128],
            Field = "outcome",
            NewValue = $"{outcome}; durationMs={stopwatch.Elapsed.TotalMilliseconds:F0}",
            UserId = user.UserId,
            At = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
