using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// "When an item enters this state, and optionally carries this label, run this playbook
/// as this agent." The conveyor belt behind the workflow's stations.
///
/// Deliberately <see cref="IAudited"/>, like <see cref="Playbook"/>: a rule is edited by
/// people, rarely, and each edit is exactly the kind of change an audit trail is for. The
/// rule itself never runs anything — <see cref="RuleFiring"/> and
/// <see cref="RunDispatcher"/> do that, in <c>Events/RuleFiringHandler</c>.
/// </summary>
public sealed class Rule : TenantEntity, IAudited
{
    public const int MaxNameLength = 100;

    public Guid ProjectId { get; init; }

    public required string Name { get; set; }

    /// <summary>The workflow state whose <em>entry</em> fires this rule.</summary>
    public Guid TriggerStateId { get; set; }

    /// <summary>When set, the item must also carry this label — otherwise entering the state is enough.</summary>
    public Guid? RequiredLabelId { get; set; }

    /// <summary>Restricted by an FK: a playbook in use by a rule cannot be deleted out from under it.</summary>
    public Guid PlaybookId { get; set; }

    public required string AgentUserId { get; set; }

    public bool Enabled { get; set; } = true;

    public required string CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; private set; }
}

/// <summary>
/// The idempotency key of <c>Events/RuleFiringHandler</c>: one row per (rule, item,
/// triggering event), inserted <c>ON CONFLICT DO NOTHING</c> before the dispatch is even
/// attempted, so a replayed <see cref="Aictiq.Modules.WorkItems.Contracts.WorkItemTransitioned"/>
/// finds its row already there and does nothing twice. Never updated except to record the
/// one outcome of that one attempt — the run it started, or why it didn't.
/// </summary>
public sealed class RuleFiring : TenantEntity
{
    public Guid RuleId { get; init; }

    public Guid ItemId { get; init; }

    public Guid EventId { get; init; }

    public required string ItemKey { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>The run this firing started, or null when it was skipped (see <see cref="SkipReason"/>).</summary>
    public Guid? RunId { get; set; }

    /// <summary>Stable, kebab-case, shown to people: <c>item-claimed</c>, <c>rule-loop</c>, etc.</summary>
    public string? SkipReason { get; set; }
}

/// <summary>Stable strings a rule firing records when it did not start a run. The frontend shows these.</summary>
public static class RuleSkipReasons
{
    public const int MaxLength = 40;

    public const string ItemClaimed = "item-claimed";
    public const string RunInProgress = "run-in-progress";
    public const string PlaybookPageMissing = "playbook-page-missing";
    public const string AgentUnavailable = "agent-unavailable";
    public const string ItemNotFound = "item-not-found";
    public const string Invalid = "invalid";
    public const string RuleLoop = "rule-loop";
    public const string ProjectReadOnly = "project-read-only";
    public const string OrganizationReadOnly = "org-read-only";
}
