using System.ComponentModel.DataAnnotations;

namespace Aictiq.Modules.Automation;

/// <summary>
/// Retention for what Automation owns. Binds the same <c>Retention</c> section Identity's
/// sweeper does, but a different class: each module prunes its own unbounded tables.
/// Finished runs' log chunks are pruned after <see cref="RunLogDays"/> by Automation's own
/// sweep; run rows are kept - they are the item's history.
/// </summary>
public sealed class AutomationRetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Log chunks of runs finished longer ago than this are deleted. 0 would keep them forever; the range refuses it.</summary>
    [Range(1, 3650)]
    public int RunLogDays { get; set; } = 30;

    /// <summary>How often the retention sweep runs.</summary>
    [Range(1, 1440)]
    public int SweepIntervalMinutes { get; set; } = 60;
}
