namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Implemented by the Tenancy module, asked by Identity at registration: "was this address
/// invited by this link?" An invitation is mailed to one address, so a person who registers
/// with that same address from that same link has already proved the mailbox is theirs -
/// the invitation <em>is</em> the confirmation, and a second mail would only be friction.
/// </summary>
public interface IInvitationLookup
{
    /// <summary>
    /// The normalized address a pending invitation was sent to, or null when the token
    /// matches nothing live: unknown, superseded by a resend, accepted, revoked or expired.
    /// </summary>
    Task<string?> FindPendingInviteeAsync(string? token, CancellationToken cancellationToken = default);
}
