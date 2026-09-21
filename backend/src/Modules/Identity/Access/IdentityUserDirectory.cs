using Microsoft.EntityFrameworkCore;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Identity.Access;

/// <summary>
/// The real <see cref="IUserDirectory"/>: Identity owns the people, so it answers
/// questions about them for every other module.
///
/// Nothing here is tenant-scoped, and that is not a hole — a user id only ever reaches
/// this from a caller that already resolved it inside a tenant (an organization's
/// membership, an item's assignee). Filtering by organization here would mean Identity
/// knowing about tenancy, which is the coupling the contracts exist to avoid.
///
/// <c>IsAgent</c> is what makes a comment by an agent never read as a comment by a person,
/// so it travels with the name rather than being looked up separately by whoever renders.
/// </summary>
public sealed class IdentityUserDirectory(IdentityDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<string, UserSummary>> GetAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<string, UserSummary>();
        }

        var ids = userIds.Distinct().ToArray();
        var users = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.AvatarKey, u.IsAgent })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            u => u.Id,
            u => new UserSummary(
                u.Id, DisplayName(u.FirstName, u.LastName, u.Email), u.AvatarKey, u.IsAgent));
    }

    public async Task<UserDirectoryPage> SearchAsync(
        IReadOnlyCollection<string> userIds,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0 || take <= 0)
        {
            return new UserDirectoryPage([], 0);
        }

        var ids = userIds.Distinct().ToArray();
        var query = db.Users.AsNoTracking().Where(u => ids.Contains(u.Id));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Email!, pattern)
                || EF.Functions.ILike(u.FirstName, pattern)
                || EF.Functions.ILike(u.LastName, pattern));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var users = await query
            // Ordered down to the id, because names are not unique and a page order that
            // is not total silently repeats and skips rows between pages.
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.Id)
            .Skip(skip)
            .Take(take)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.AvatarKey, u.IsAgent })
            .ToListAsync(cancellationToken);

        return new UserDirectoryPage(
            [.. users.Select(u => new UserContact(
                u.Id, DisplayName(u.FirstName, u.LastName, u.Email), u.Email ?? "",
                u.AvatarKey, u.IsAgent))],
            totalCount);
    }

    public async Task<IReadOnlySet<string>> FilterAgentsAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var ids = userIds.Distinct().ToArray();
        return (await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsAgent)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<UserContact?> FindByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        // NormalizedEmail is what ASP.NET Identity indexes, and it is upper-invariant —
        // matching on Email with ILike would be a sequential scan of every account on the
        // instance, on a path an anonymous caller can reach.
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.AvatarKey, u.IsAgent })
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new UserContact(
                user.Id, DisplayName(user.FirstName, user.LastName, user.Email), user.Email ?? "",
                user.AvatarKey, user.IsAgent);
    }

    public async Task<IReadOnlyDictionary<string, UserDeliveryProfile>> GetDeliveryProfilesAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0) return new Dictionary<string, UserDeliveryProfile>();
        var ids = userIds.Distinct().ToArray();
        return (await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.TimeZone, u.IsAgent })
            .ToListAsync(cancellationToken))
            .Where(u => !string.IsNullOrWhiteSpace(u.Email))
            .ToDictionary(u => u.Id, u => new UserDeliveryProfile(u.Id,
                DisplayName(u.FirstName, u.LastName, u.Email), u.Email!, u.TimeZone, u.IsAgent));
    }

    /// <summary>
    /// Never blank: someone who has not filled in a name would otherwise render as an
    /// empty row with a menu beside it, and there would be no way to tell which one to
    /// remove.
    /// </summary>
    private static string DisplayName(string firstName, string lastName, string? email)
    {
        var name = $"{firstName} {lastName}".Trim();
        return name.Length > 0 ? name : email ?? "";
    }
}
