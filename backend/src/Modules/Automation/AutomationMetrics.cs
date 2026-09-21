using System.Diagnostics.Metrics;

namespace Aictiq.Modules.Automation;

/// <summary>
/// The factory's operational metrics. <c>automation.runs.runner_lost</c> exists to be
/// alerted on: a lost runner means an agent's machine died mid-work, and nothing retries
/// it on its own.
/// </summary>
public static class AutomationMetrics
{
    public const string MeterName = "Aictiq.Automation";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Started = Meter.CreateCounter<long>(
        "automation.runs.started", unit: "{run}", description: "Factory runs dispatched.");

    public static readonly Counter<long> Finished = Meter.CreateCounter<long>(
        "automation.runs.finished", unit: "{run}", description: "Factory runs that reached a terminal state.");

    public static readonly Counter<long> RunnerLost = Meter.CreateCounter<long>(
        "automation.runs.runner_lost", unit: "{run}",
        description: "Runs failed because their runner stopped answering; alert on this.");

    public static KeyValuePair<string, object?> OutcomeTag(string outcome) => new("outcome", outcome);
}
