using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.SharedKernel.Authorization;

/// <summary>
/// Endpoint filters for the two questions almost every endpoint asks: is the caller a
/// member with a high enough role, and does their token allow this kind of action.
///
/// The status codes are the important part and they are not interchangeable:
///
/// <list type="bullet">
/// <item><b>404</b> when the caller is not a member of the organization or project, or
/// when it does not exist. Answering 403 would confirm that a project with that key
/// exists - an information leak across tenants, and the reason
/// <c>404, not 403</c> is a repository-wide rule.</item>
/// <item><b>403</b> only once the caller can already see the resource: they are a member,
/// but their role or token scope does not stretch to this action. Nothing is revealed
/// that they could not already see.</item>
/// </list>
/// </summary>
public static class AuthorizationFilters
{
    /// <summary>Where <see cref="RequireProjectRole"/> leaves the project it resolved.</summary>
    public const string ProjectItemKey = "aictiq.project";

    /// <summary>
    /// The project the route named, resolved once by the role filter so neither the
    /// writable filter nor the endpoint looks it up again.
    /// </summary>
    public static ProjectRef? ResolvedProject(this HttpContext context) =>
        context.Items.TryGetValue(ProjectItemKey, out var value) && value is ProjectRef project
            ? project
            : null;

    public static Guid? ResolvedProjectId(this HttpContext context) => context.ResolvedProject()?.Id;

    public static TBuilder RequireOrgRole<TBuilder>(this TBuilder builder, OrgRole required)
        where TBuilder : IEndpointConventionBuilder
        =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var user = http.RequestServices.GetRequiredService<ICurrentUser>();
            var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();

            if (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId)
            {
                return NotAMember();
            }

            var access = http.RequestServices.GetRequiredService<IProjectAccess>();
            var role = await access.GetOrgRoleAsync(userId, organizationId, http.RequestAborted);

            if (role is null)
            {
                return NotAMember();
            }

            return role.Value.Satisfies(required)
                ? await next(context)
                : InsufficientRole($"This action requires the {required} organization role or higher.");
        });

    /// <summary>
    /// Refuses a caller who may not operate the AI software factory. Sits beside
    /// <c>RequireOrgRole</c> or <c>RequireProjectRole</c>, never instead of them: those decide
    /// whether the caller may see the thing at all (and answer 404 when not), this decides
    /// whether a member who can see it may also drive the agents. So a non-member is still a
    /// 404 here, and only an established member gets the 403.
    /// </summary>
    public static TBuilder RequireFactoryOperator<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var refusal = await FactoryOperatorRefusalAsync(http);
            return refusal ?? await next(context);
        });

    /// <summary>
    /// The same decision for endpoints that cannot carry a filter: an MCP tool, a handler that
    /// resolves its organization from a record. Null means go ahead.
    /// </summary>
    public static async Task<IResult?> FactoryOperatorRefusalAsync(HttpContext http)
    {
        var user = http.RequestServices.GetRequiredService<ICurrentUser>();
        var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();
        if (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId)
        {
            return NotAMember();
        }

        var access = http.RequestServices.GetRequiredService<IProjectAccess>();
        if (await access.GetOrgRoleAsync(userId, organizationId, http.RequestAborted) is null)
        {
            return NotAMember();
        }

        return await access.CanOperateFactoryAsync(userId, organizationId, http.RequestAborted)
            ? null
            : Results.Problem(
                title: "Not permitted to operate the factory.",
                detail: "Starting, cancelling and reading AI runs is limited to members an administrator has allowed to operate the factory.",
                type: ProblemTypes.FactoryNotPermitted,
                statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Reads the <c>{projectKey}</c> route value, resolves it inside the current
    /// organization, and checks the caller's project role.
    /// </summary>
    public static TBuilder RequireProjectRole<TBuilder>(this TBuilder builder, ProjectRole required)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var user = http.RequestServices.GetRequiredService<ICurrentUser>();
            var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();

            if (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId)
            {
                return NotAMember();
            }

            if (http.GetRouteValue("projectKey") is not string projectKey || projectKey.Length == 0)
            {
                throw new InvalidOperationException(
                    "RequireProjectRole is on an endpoint with no {projectKey} route parameter.");
            }

            var access = http.RequestServices.GetRequiredService<IProjectAccess>();
            var project = await access.FindProjectAsync(organizationId, projectKey, http.RequestAborted);
            if (project is null)
            {
                // No such project *in this organization* - indistinguishable, on purpose,
                // from a project the caller may not see.
                return NotAMember();
            }

            var role = await access.GetProjectRoleAsync(userId, project.Id, http.RequestAborted);
            if (role is null)
            {
                return NotAMember();
            }

            if (!role.Value.Satisfies(required))
            {
                return InsufficientRole($"This action requires the {required} project role or higher.");
            }

            http.Items[ProjectItemKey] = project;
            return await next(context);
        });

    /// <summary>
    /// Refuses writes to an archived project with <b>409</b>.
    ///
    /// Archiving is how a team retires a project without deleting its history, so the
    /// rows stay readable and every write path has to be closed - one filter beside
    /// <see cref="RequireProjectRole"/> rather than a check each endpoint remembers.
    /// 409 rather than 403 because the caller's permissions are fine: un-archiving makes
    /// the identical request succeed.
    ///
    /// Must follow <see cref="RequireProjectRole"/>, which is what resolved the project.
    ///
    /// The same filter closes an organization whose payment failed and whose grace period
    /// has run out, for the same reason and with the same shape: reads keep
    /// working, writes answer 409 with a distinct type, and paying makes the request succeed.
    /// Billing answers that question through <see cref="IOrganizationBillingState"/>; a
    /// self-hosted instance answers it without touching the database.
    /// </summary>
    public static TBuilder RequireProjectWritable<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var project = http.ResolvedProject()
                ?? throw new InvalidOperationException(
                    "RequireProjectWritable is on an endpoint that does not RequireProjectRole first.");

            return await ProjectWriteRefusalAsync(http, project.Key, project.IsArchived) ?? await next(context);
        });

    /// <summary>
    /// What <see cref="RequireProjectWritable"/> answers, for handlers that reach their
    /// project through a record - a sprint, a team's board, a wiki page by id - rather
    /// than through a <c>{projectKey}</c> route value. Null means the write may proceed.
    /// </summary>
    public static async Task<IResult?> ProjectWriteRefusalAsync(HttpContext http, string projectKey, bool isArchived)
    {
        if (isArchived)
        {
            return Results.Problem(
                title: "This project is archived.",
                detail: $"{projectKey} is read-only until it is un-archived.",
                type: ProblemTypes.ProjectArchived,
                statusCode: StatusCodes.Status409Conflict);
        }

        var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();
        var billing = http.RequestServices.GetRequiredService<IOrganizationBillingState>();
        if (tenant.OrganizationId is { } organizationId
            && await billing.IsReadOnlyAsync(organizationId, http.RequestAborted))
        {
            return Results.Problem(
                title: "This organization is read-only.",
                detail: "The evaluation has ended, or a payment failed and the grace period with "
                    + "it. Everything is still readable, downloadable and exportable; an owner can "
                    + "choose a plan or update the payment method under Settings → Billing to make "
                    + "changes again.",
                type: ProblemTypes.OrganizationReadOnly,
                statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    /// <summary>
    /// Requires a personal access token scope. A browser session carries no scopes and is
    /// unaffected - scopes narrow a token below its owner's permissions, they never grant.
    /// </summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var user = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();

            return ScopeRequirements.IsSatisfiedBy(user.Scopes, scope)
                ? await next(context)
                : Results.Problem(
                    title: "Insufficient token scope.",
                    detail: $"This endpoint requires the '{scope}' scope.",
                    type: ProblemTypes.InsufficientScope,
                    statusCode: StatusCodes.Status403Forbidden);
        });

    private static IResult NotAMember() =>
        Results.Problem(
            title: "Not found.",
            // Deliberately says nothing about whether the resource exists.
            detail: "The resource does not exist, or you do not have access to it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InsufficientRole(string detail) =>
        Results.Problem(
            title: "Insufficient permissions.",
            detail: detail,
            type: ProblemTypes.InsufficientRole,
            statusCode: StatusCodes.Status403Forbidden);
}
