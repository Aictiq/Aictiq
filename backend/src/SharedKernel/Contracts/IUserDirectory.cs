namespace Aictiq.SharedKernel.Contracts;

/// <summary>What any module needs to render a person: no email, no roles, no secrets.</summary>
public sealed record UserSummary(string Id, string DisplayName, string? AvatarKey, bool IsAgent);

/// <summary>
/// A person as an <b>administrator of their own organization</b> sees them - the same
/// fields plus the email address.
///
/// Separate from <see cref="UserSummary"/> on purpose: the email is the one field here
/// that is not safe to render everywhere, and a screen that shows one must have gone
/// looking for it. Whoever asks is responsible for the check (org members list: Guests
/// do not get it).
/// </summary>
public sealed record UserContact(
    string Id, string DisplayName, string Email, string? AvatarKey, bool IsAgent);

/// <summary>The minimal private profile needed by a background delivery service.</summary>
public sealed record UserDeliveryProfile(
    string Id, string DisplayName, string Email, string? TimeZone, bool IsAgent);

/// <param name="TotalCount">Matches across the whole candidate set, not just this page.</param>
public sealed record UserDirectoryPage(IReadOnlyList<UserContact> Items, int TotalCount);

/// <summary>
/// Implemented by Identity. Batch by design: a list of fifty work items needs fifty
/// assignees resolved, and doing that one query at a time is the classic N+1 that only
/// shows up under real data.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyDictionary<string, UserSummary>> GetAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Filters, orders and pages a caller-supplied set of people - in practice the
    /// membership of one organization.
    ///
    /// The candidate ids come in rather than the search going out because names and email
    /// addresses live in Identity's schema and memberships live in Tenancy's, and modules
    /// do not join across schemas. Matching and ordering therefore happen where the text
    /// is, and only a page of rows comes back - rather than the caller hydrating every
    /// member of the organization to show twenty-five of them.
    /// </summary>
    /// <param name="userIds">Who may appear at all. An empty set matches nothing.</param>
    /// <param name="search">Substring of name or email, case-insensitive. Null lists everyone.</param>
    Task<UserDirectoryPage> SearchAsync(
        IReadOnlyCollection<string> userIds,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of these ids belong to agents.
    ///
    /// A filter such as <c>claimed:@agent</c> has to be applied before paging, so it needs
    /// the answer for every candidate at once. The candidate ids come in rather than the
    /// question going out for the same reason <see cref="SearchAsync"/> works that way:
    /// <c>is_agent</c> lives in Identity's schema and the items live in WorkItems', and
    /// modules do not join across schemas. An empty set matches nothing.
    /// </summary>
    Task<IReadOnlySet<string>> FilterAgentsAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account an email address belongs to, or null if nobody has registered it.
    ///
    /// Invitations are addressed to an <em>address</em>, not to an account - the whole
    /// point is that the person may not have one yet - so the module that sends them
    /// needs exactly this question answered to tell "already a member here" from "a
    /// stranger". Matching is case-insensitive, because an address is.
    ///
    /// Not an enumeration endpoint and must never become one: it is reached only from
    /// code that already holds the address it is asking about.
    /// </summary>
    Task<UserContact?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves delivery addresses in one query. This deliberately lives beside the other
    /// directory operations rather than letting Notifications reach into Identity's schema.
    /// </summary>
    Task<IReadOnlyDictionary<string, UserDeliveryProfile>> GetDeliveryProfilesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);
}
