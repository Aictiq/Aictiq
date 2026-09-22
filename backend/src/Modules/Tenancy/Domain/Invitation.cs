using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Tenancy.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Tenancy.Domain;

/// <summary>What an open invitation is doing, as far as anyone looking at it is concerned.</summary>
public enum InvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Revoked = 2,
    Expired = 3,
}

/// <summary>
/// A standing offer of membership, addressed to an <b>email address</b> rather than to an
/// account - the person being invited usually does not have one yet, which is the entire
/// reason invitations exist rather than an "add member" button.
///
/// The link is the credential. Only its SHA-256 hash is stored, exactly as refresh tokens
/// are: a leaked database must not hand out working invitations, and there is nothing to
/// compare a presented token against except a hash. That has one consequence worth
/// stating out loud - <b>Aictiq cannot re-send the original link</b>, because it does not
/// have it. Resending therefore mints a new token and retires the old one (see
/// <see cref="Reissue"/>).
/// </summary>
public sealed class Invitation : TenantEntity, IAudited
{
    /// <summary>Long enough that guessing is not a strategy; short enough to paste into a chat.</summary>
    public const int TokenBytes = 32;

    /// <summary>The Notifications template this asks for. Rendering happens there, never here.</summary>
    public const string EmailTemplate = "invitation";

    /// <summary>
    /// A week. Long enough to survive a holiday, short enough that a link found in an old
    /// inbox two years from now is not a way into the organization.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Lower-cased on the way in - an address is case-insensitive, and the partial unique index is not.</summary>
    public required string Email { get; set; }

    public OrgRole Role { get; set; }

    /// <summary>Optional: the invitation can also drop them straight into one project.</summary>
    public Guid? ProjectId { get; set; }

    public ProjectRole? ProjectRole { get; set; }

    /// <summary>
    /// Copied onto the membership when the invitation is accepted. False for a
    /// stakeholder, always false for a Guest (<c>ck_invitations_guest_not_operator</c>).
    /// </summary>
    public bool CanOperateFactory { get; set; } = true;

    public byte[] TokenHash { get; set; } = [];

    public required string InvitedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// The account that actually used the link. Kept alongside <see cref="Email"/> rather
    /// than instead of it: an invitation sent to one address and accepted by someone
    /// signed in as another is allowed - people forward mail, and refusing would strand
    /// them - but the pair is the record that it happened.
    /// </summary>
    public string? AcceptedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public InvitationStatus StatusAt(DateTimeOffset now) =>
        AcceptedAt is not null ? InvitationStatus.Accepted
        : RevokedAt is not null ? InvitationStatus.Revoked
        : ExpiresAt <= now ? InvitationStatus.Expired
        : InvitationStatus.Pending;

    /// <param name="token">The plaintext, returned to the caller. It is never stored and never recoverable.</param>
    public static Invitation Create(
        Guid organizationId, string email, OrgRole role, Guid? projectId, ProjectRole? projectRole,
        bool canOperateFactory, string invitedBy, DateTimeOffset now, out string token)
    {
        token = NewToken();
        return new Invitation
        {
            OrganizationId = organizationId,
            Email = Normalize(email),
            Role = role,
            ProjectId = projectId,
            ProjectRole = projectRole,
            CanOperateFactory = role != OrgRole.Guest && canOperateFactory,
            TokenHash = HashToken(token),
            InvitedBy = invitedBy,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
    }

    /// <summary>
    /// Mints a fresh link and restarts the clock. The previous token stops working, which
    /// is not a side effect to apologise for: the usual reason to resend is that the first
    /// link went astray, and a link that went astray is one that should stop working.
    /// </summary>
    public void Reissue(DateTimeOffset now, out string token)
    {
        token = NewToken();
        TokenHash = HashToken(token);
        ExpiresAt = now + Lifetime;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt = now;

    /// <summary>
    /// Announces an acceptance that the database has <em>already</em> recorded - the
    /// accept is a single conditional UPDATE, so there is no in-memory state change to
    /// make here. Raising it on the tracked row is what puts the event in the outbox
    /// inside the same transaction as the new membership, so nothing downstream can hear
    /// about a join that then rolled back.
    /// </summary>
    public void MarkAccepted(string userId, DateTimeOffset now) =>
        Raise(new InvitationAccepted(OrganizationId, Id, Email, userId, Role) { OccurredAt = now });

    /// <summary>
    /// Asks for the invitation email. It goes through the outbox like every other one, so
    /// an unreachable relay cannot fail the request that created the invitation - and on
    /// an instance with no relay at all the row is parked as <c>skipped</c> and the
    /// inviter shares the link instead.
    /// </summary>
    public void RequestEmail(IReadOnlyDictionary<string, string> variables) =>
        Raise(new SendEmailRequested(Email, EmailTemplate, variables));

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Base64url so the token survives a URL, an email client's line wrapping and a copy
    /// out of a terminal without an encoding step that someone will get wrong.
    /// </summary>
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
