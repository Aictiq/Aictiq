using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// Establishes which organization the request belongs to, before any endpoint runs.
///
/// Two sources, in order of precedence:
///
/// <list type="number">
/// <item>The <c>{orgSlug}</c> route value. A person can belong to several organizations
/// on one session, so the URL - not the credential - decides which one they are acting
/// in.</item>
/// <item>The <c>org</c> claim, for credentials bound to a single organization: personal
/// access tokens, and therefore agents.</item>
/// </list>
///
/// Membership is verified here rather than in each endpoint, and a non-member gets a
/// <b>404</b>: a 403 would confirm that an organization with that slug exists, which is
/// exactly what a stranger probing slugs wants to learn.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>The route parameter every organization-scoped route carries.</summary>
    public const string OrgSlugRouteValue = "orgSlug";

    public async Task InvokeAsync(
        HttpContext context,
        AmbientCurrentTenant tenant,
        ICurrentUser user,
        IOrganizationLookup organizations,
        IProjectAccess access)
    {
        var slug = context.GetRouteValue(OrgSlugRouteValue) as string;

        if (!string.IsNullOrEmpty(slug))
        {
            var organization = await organizations.FindBySlugAsync(slug, context.RequestAborted);
            if (organization is null)
            {
                await NotFoundAsync(context);
                return;
            }

            // RLS needs the candidate organization before the membership query can run.
            // This is not authorization: the query still has the caller's user-id
            // predicate and a non-member gets the same 404 as an unknown slug. It merely
            // lets Postgres enforce the same boundary while we establish membership.
            tenant.Set(organization.Id);

            // A slug in the URL is a request, not a grant: membership decides.
            if (user.UserId is not { } userId
                || await access.GetOrgRoleAsync(userId, organization.Id, context.RequestAborted) is null)
            {
                await NotFoundAsync(context);
                return;
            }

            // A token bound to one organization must not be usable against another, even
            // if its owner is a member of both - that is the whole point of binding it.
            if (user.OrganizationId is { } bound && bound != organization.Id)
            {
                await NotFoundAsync(context);
                return;
            }
        }
        else if (user.OrganizationId is { } claimed)
        {
            tenant.Set(claimed);
        }

        await next(context);
    }

    private static Task NotFoundAsync(HttpContext context) =>
        Results.Problem(
            title: "Not found.",
            detail: "The organization does not exist, or you are not a member of it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound)
        .ExecuteAsync(context);
}

public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>
    /// Must come after <c>UseAuthentication</c> - membership needs a principal - and
    /// after routing, since the organization slug is a route value.
    /// </summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}
