using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Tenancy.Access;

/// <summary>
/// The real <see cref="IInvitationLookup"/>. Reads across tenants by token hash, exactly as
/// the invitation preview does: there is no tenant yet, resolving one is what the token is
/// for, and the row-level security policy admits the read only while that exact hash is
/// in the session context.
/// </summary>
public sealed class InvitationLookup(
    TenancyDbContext db, AmbientCurrentTenant tenant, TimeProvider timeProvider) : IInvitationLookup
{
    public async Task<string?> FindPendingInviteeAsync(
        string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var hash = Invitation.HashToken(token);
        using var capability = tenant.UseInvitationTokenHash(Convert.ToHexString(hash));
        var invitation = await db.Invitations.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);

        return invitation?.StatusAt(timeProvider.GetUtcNow()) == InvitationStatus.Pending
            ? invitation.Email
            : null;
    }
}
