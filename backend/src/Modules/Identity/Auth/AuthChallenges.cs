using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;

namespace Aictiq.Modules.Identity.Auth;

/// <summary>
/// Issues, finds and spends <see cref="AuthChallenge"/> tickets. Spending is a single
/// conditional UPDATE, as for every one-time credential here: two requests carrying one
/// ticket must produce one sign-in and one refusal.
/// </summary>
internal static class AuthChallenges
{
    public static async Task<(AuthChallenge Challenge, string Ticket)> IssueAsync(
        IdentityDbContext db, AuthChallengePurpose purpose, string? userId, string? state,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var challenge = AuthChallenge.Create(purpose, userId, state, now, out var ticket);
        db.AuthChallenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);
        return (challenge, ticket);
    }

    /// <summary>The live ticket for this purpose, or null - expired, spent and made-up are all one answer.</summary>
    public static async Task<AuthChallenge?> FindLiveAsync(
        IdentityDbContext db, string? ticket, AuthChallengePurpose purpose, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return null;
        }

        var hash = AuthChallenge.Hash(ticket);
        return await db.AuthChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TicketHash == hash
                && c.Purpose == purpose
                && c.UsedAt == null
                && c.ExpiresAt > now, cancellationToken);
    }

    /// <summary>Spends the ticket. False when another request already did, or it ran out meanwhile.</summary>
    public static async Task<bool> ConsumeAsync(
        IdentityDbContext db, AuthChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.AuthChallenges
            .Where(c => c.Id == challenge.Id && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken) == 1;
}
