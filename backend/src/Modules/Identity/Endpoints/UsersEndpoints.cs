using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.Modules.Identity.Endpoints;

public sealed record UserView(
    string Id, string Email, string FirstName, string LastName, bool IsActive, DateTimeOffset CreatedAt);

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/admin/users").WithTags("Users")
            .RequireAuthorization(Policies.Admin);

        group.MapGet("/", async (
            IdentityDbContext db,
            int page,
            int pageSize,
            string? search,
            CancellationToken cancellationToken) =>
        {
            var paging = new PageRequest(page == 0 ? 1 : page, pageSize == 0 ? 25 : pageSize);

            var query = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                query = query.Where(u =>
                    EF.Functions.ILike(u.Email!, pattern) ||
                    EF.Functions.ILike(u.FirstName, pattern) ||
                    EF.Functions.ILike(u.LastName, pattern));
            }

            var totalCount = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderBy(u => u.Email)
                .Skip(paging.Skip)
                .Take(paging.NormalizedPageSize)
                .Select(u => new UserView(u.Id, u.Email!, u.FirstName, u.LastName, u.IsActive, u.CreatedAt))
                .ToListAsync(cancellationToken);

            return Results.Ok(new PagedResult<UserView>(
                items, paging.NormalizedPage, paging.NormalizedPageSize, totalCount));
        });

        group.MapPost("/{id}/deactivate", async (
            string id,
            UserManager<ApplicationUser> userManager,
            Auth.ITokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var user = await userManager.FindByIdAsync(id);
            if (user is null)
            {
                return Results.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
            }

            user.IsActive = false;
            await userManager.UpdateAsync(user);
            // A deactivated user must not be able to mint new access tokens.
            await tokenService.RevokeAllForUserAsync(id, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }
}
