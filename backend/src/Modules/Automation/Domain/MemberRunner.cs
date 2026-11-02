using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Automation.Domain;

/// <summary>
/// The runner one project member's own runs go to, set by a project Admin under Settings → Members.
/// For someone who is not an Admin it is a lock: every chat, refine and Start run they start uses it,
/// whatever runner the request names. For an Admin it is only what their pickers preselect. Runs a
/// person did not start - rules, chains, mentions - never consult it. A runner deleted or disabled
/// since no longer applies, and the member's runs go where they would without it.
/// </summary>
public sealed class ProjectMemberRunner : TenantEntity, IAudited
{
    public Guid ProjectId { get; init; }
    public required string UserId { get; init; }
    public Guid RunnerId { get; set; }
    public required string AssignedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
