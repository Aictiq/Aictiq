namespace Aictiq.SharedKernel;

/// <summary>
/// The `type` URIs on RFC 9457 problem responses.
///
/// A stable catalogue rather than ad-hoc strings: `type` is the field a client is
/// supposed to branch on, so it is part of the API contract. Changing one of these is a
/// breaking change; adding one is not.
/// </summary>
public static class ProblemTypes
{
    private const string Base = "https://aictiq.com/problems/";

    /// <summary>409 — someone else saved first, or a referenced record moved.</summary>
    public const string Conflict = Base + "conflict";

    /// <summary>409 — moving a card would exceed its destination column's WIP limit.</summary>
    public const string WipLimit = Base + "wip-limit";

    /// <summary>400 — field-level validation failures in `errors`.</summary>
    public const string Validation = Base + "validation";

    /// <summary>403 — a cookie-authenticated write arrived without `X-Aictiq-Request`.</summary>
    public const string CsrfHeaderMissing = Base + "csrf-header-missing";

    /// <summary>402 — the action is refused by the organization's plan limits.</summary>
    public const string PlanLimit = Base + "plan-limit";

    /// <summary>
    /// 409 — the organization's payment failed and its grace period has ended, so it is
    /// read-only. A conflict rather than a 403, like <see cref="ProjectArchived"/>: the
    /// caller's permissions are fine, and paying makes the identical request succeed.
    /// </summary>
    public const string OrganizationReadOnly = Base + "org-read-only";

    /// <summary>
    /// 409 — a plan change was refused because current usage exceeds the target plan. The
    /// response carries <c>exceeded</c>: one <c>{ limit, used, allowed }</c> per limit, so
    /// a client can say exactly what to remove first.
    /// </summary>
    public const string PlanDowngradeBlocked = Base + "plan-downgrade-blocked";

    /// <summary>
    /// 409 — this instance does not take payments: it is self-hosted, or Stripe is not
    /// configured. Nothing the caller can change; the client should hide billing actions.
    /// </summary>
    public const string BillingUnavailable = Base + "billing-unavailable";

    /// <summary>
    /// 409 — the project is archived and therefore read-only. A conflict rather than a
    /// 403: nothing is wrong with the caller's permissions, and un-archiving makes the
    /// same request succeed.
    /// </summary>
    public const string ProjectArchived = Base + "project-archived";

    /// <summary>
    /// 404 — the caller is not a member of the organization or project. A 404 on purpose:
    /// a 403 would confirm the resource exists.
    /// </summary>
    public const string NotAMember = Base + "not-a-member";

    /// <summary>403 — the token is valid but lacks the scope this endpoint needs.</summary>
    public const string InsufficientScope = Base + "insufficient-scope";

    /// <summary>
    /// 401 — the personal access token was revoked or has expired. Distinct from a plain
    /// 401 because the remedy is different: nothing about retrying or refreshing will help,
    /// and an agent looping on one needs to be told to stop rather than to try again.
    /// </summary>
    public const string TokenRevoked = Base + "token-revoked";

    /// <summary>403 — the caller is a member, but their role is not high enough.</summary>
    public const string InsufficientRole = Base + "insufficient-role";

    /// <summary>
    /// 403 — the caller is a member and may see the record, but may not operate the AI
    /// software factory: start, cancel or read the log of a run. A 403 rather than
    /// a 404 because the item is theirs to see; only driving the agents is not.
    /// </summary>
    public const string FactoryNotPermitted = Base + "factory-not-permitted";

    /// <summary>403 — this self-hosted instance accepts members only by invitation.</summary>
    public const string OrganizationCreationDisabled = Base + "organization-creation-disabled";

    /// <summary>
    /// 409 — the item already has a live claim or a live run, so a factory run cannot
    /// start on it. The database's partial unique index on live runs is what makes this
    /// a guarantee rather than a check the dispatch endpoint remembers.
    /// </summary>
    public const string ItemClaimed = Base + "item-claimed";

    /// <summary>
    /// 413 — the runner sent more log than the per-run cap it was told about
    /// (<c>Automation:MaxLogBytes</c>). The runner stops at the cap; a server that has to
    /// refuse one was misconfigured or misbehaving.
    /// </summary>
    public const string LogLimitExceeded = Base + "log-limit-exceeded";

    /// <summary>
    /// 409 — the write would have left an organization with no Owner. Raised by the
    /// database (<see cref="Persistence.DatabaseSignals.LastOwner"/>), not by the
    /// endpoint: two Owners demoting each other at the same moment both pass any check
    /// the application can make on its own.
    /// </summary>
    public const string LastOwner = Base + "last-owner";

    /// <summary>
    /// 409 — the playbook is named by an automation rule and cannot be deleted
    /// while the rule exists. The FK is <c>RESTRICT</c>; this is the friendly front door
    /// for the violation GlobalExceptionHandler would otherwise turn into a generic conflict.
    /// </summary>
    public const string PlaybookInUse = Base + "playbook-in-use";
}
