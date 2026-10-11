using System.ComponentModel.DataAnnotations;

namespace Aictiq.Modules.Automation;

/// <summary>
/// What the server tells a runner to expect (<c>POST /runner/hello</c>) and how it judges one
/// alive. Every value has a default: a self-hosted instance configures nothing here.
/// </summary>
public sealed class AutomationOptions
{
    public const string SectionName = "Automation";

    /// <summary>How often a runner should call <c>/runner/heartbeat</c> while it is up.</summary>
    [Range(10, 600)]
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>How long <c>/runner/runs/claim</c> holds a poll open.</summary>
    [Range(1, 55)]
    public int PollTimeoutSeconds { get; set; } = 25;

    /// <summary>Log bytes kept per run; a runner stops sending at this cap.</summary>
    [Range(1024, 1_073_741_824)]
    public int MaxLogBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>The largest single log batch a runner may send.</summary>
    [Range(1024, 16 * 1024 * 1024)]
    public int MaxLogBatchBytes { get; set; } = 64 * 1024;

    /// <summary>The ceiling on any playbook's time limit.</summary>
    [Range(5, 1440)]
    public int MaxRunMinutes { get; set; } = 720;

    /// <summary>
    /// A run whose last heartbeat (or assignment, if it never spoke) is older than this
    /// has lost its runner: the sweeper fails it and frees the item rather than leaving a
    /// run live forever.
    /// </summary>
    [Range(1, 120)]
    public int RunnerLostAfterMinutes { get; set; } = 5;

    /// <summary>
    /// Fleet mode: how long a due run is left for a matching runner with more free capacity
    /// before a busier one that also matches may take it. Long enough for an idle runner's
    /// next poll, short enough that a runner that is online but not polling never strands a run.
    /// </summary>
    [Range(0, 300)]
    public int FleetBalanceGraceSeconds { get; set; } = 15;

    /// <summary>An ephemeral runner not seen for this long is removed by the sweeper (it normally removes itself).</summary>
    [Range(5, 1440)]
    public int EphemeralRunnerLostAfterMinutes { get; set; } = 15;

    /// <summary>Lets the scale webhook reach private addresses. Development and tests only.</summary>
    public bool FleetWebhookAllowPrivateNetworks { get; set; }

    /// <summary>
    /// <c>last_seen_at</c> is written at most this often. The throttle is in the <c>WHERE</c>
    /// clause, so concurrent heartbeats from one runner do not each decide to write.
    /// </summary>
    public TimeSpan LastSeenThrottle => TimeSpan.FromSeconds(Math.Min(30, HeartbeatIntervalSeconds / 2));

    /// <summary>A runner is online when it was seen within two heartbeats plus the write throttle.</summary>
    public TimeSpan OnlineWindow => TimeSpan.FromSeconds(HeartbeatIntervalSeconds * 2) + LastSeenThrottle;
}
