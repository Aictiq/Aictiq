using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Identity.Access;

/// <summary>Looks up an author without leaking external-login data outside Identity.</summary>
public sealed class IdentityExternalLoginLookup(IdentityDbContext db) : IExternalLoginLookup
{
    public async Task<string?> FindGitHubUserIdAsync(string? login, string? email, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(login))
        {
            var userId = await db.Set<IdentityUserLogin<string>>().AsNoTracking()
                .Where(x => x.LoginProvider == "github" && x.ProviderKey == login)
                .Select(x => x.UserId).SingleOrDefaultAsync(cancellationToken);
            if (userId is not null) return userId;
        }
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToUpperInvariant();
        return await db.Users.AsNoTracking().Where(x => x.NormalizedEmail == normalized)
            .Select(x => x.Id).SingleOrDefaultAsync(cancellationToken);
    }
}
