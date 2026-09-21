using Microsoft.AspNetCore.Identity;
using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Identity.Domain;

/// <summary>
/// A person — or an agent.
///
/// Agents are the <em>same table</em> on purpose: every assignee, author, reviewer and
/// audit column in the product points at a user id, and a parallel "bot" concept would
/// mean every one of those columns learning about a second kind of thing. Making them
/// users means the whole product already works with them, and the only rule that has to
/// exist anywhere else is "an agent is visibly an agent".
/// </summary>
public sealed class ApplicationUser : IdentityUser, IAudited
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// True for an agent identity. It authenticates only with a personal access token —
    /// the password endpoints refuse it outright — and it is labelled as an agent
    /// everywhere it appears, so a comment from one never reads as a comment from a person.
    /// </summary>
    public bool IsAgent { get; set; }

    /// <summary>
    /// The person answerable for an agent. Always set for an agent and always null for a
    /// person — a check constraint says so, because the pair is what makes "who let this
    /// thing into the repository" answerable a year later.
    ///
    /// When an owner leaves an organization their agents are handed to one of its Owners
    /// rather than orphaned; see <c>AgentOwnershipHandler</c>.
    /// </summary>
    public string? AgentOwnerUserId { get; set; }

    /// <summary>Object-storage key for the avatar, or null for the generated initials.</summary>
    public string? AvatarKey { get; set; }

    /// <summary>
    /// IANA time zone id (<c>Europe/Sarajevo</c>), or null to follow the organization's.
    ///
    /// A person's, not a team's: someone on a team in Berlin who is themselves in Lisbon
    /// wants their own due dates to read in their own hours. The team's zone is the one
    /// planning uses, and the two are deliberately different settings.
    /// </summary>
    public string? TimeZone { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
