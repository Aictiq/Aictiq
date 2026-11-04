using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Automation.Access;

/// <summary>
/// Who may read a run in full - its log, prompt, summary and failure reason - rather than
/// only that it ran, when and on what. An organization Admin or Owner reads every run; anyone
/// else reads only the runs they started. A run carries its starter forward: an automatic
/// continue, a follow-up stage, a rework and a repository wave all keep the requester of the
/// run they came from, so the person who started the work reads the whole of it. A run an
/// automation rule started has no person behind it, and only Admins read it in full.
/// </summary>
public static class RunPrivacy
{
    public static bool SeesInFull(Run run, string? viewerId, bool isOrgAdmin) =>
        SeesInFull(run.RequestedBy, viewerId, isOrgAdmin);

    public static bool SeesInFull(string? requestedBy, string? viewerId, bool isOrgAdmin) =>
        isOrgAdmin || (requestedBy is not null && requestedBy == viewerId);

    /// <summary>Whether <paramref name="userId"/> is an Admin or Owner of the organization.</summary>
    public static async Task<bool> IsOrgAdminAsync(
        IProjectAccess access, string? userId, Guid organizationId, CancellationToken ct) =>
        userId is not null
        && await access.GetOrgRoleAsync(userId, organizationId, ct) is { } role
        && role.Satisfies(OrgRole.Admin);
}
