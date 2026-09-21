using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.WorkItems.Endpoints;

/// <summary>
/// The item-scoped routes (<c>/orgs/{orgSlug}/items/{itemKey}/…</c>) carry no
/// <c>{projectKey}</c>, so <c>RequireProjectWritable</c> cannot run for them and, until this
/// filter, an archived project's items stayed writable through them. This resolves the
/// project from the item key's prefix and answers the same two 409s the project-scoped
/// filter does: archived, and organization read-only — an expired evaluation or a failed
/// payment whose grace period has ended.
///
/// It does not decide visibility — an unknown key falls through to the handler, whose
/// <c>FindVisible</c> answers 404 without confirming that the project exists.
/// </summary>
public static class ItemRouteFilters
{
    public static TBuilder RequireItemProjectWritable<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((_, next) => async context =>
        {
            var http = context.HttpContext;
            var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();
            if (tenant.OrganizationId is not { } organizationId
                || http.GetRouteValue("itemKey") is not string itemKey)
            {
                return await next(context);
            }

            var (projectKey, number) = WorkItemEndpoints.ParseKey(itemKey);
            if (number is null)
            {
                return await next(context);
            }

            var access = http.RequestServices.GetRequiredService<IProjectAccess>();
            var project = await access.FindProjectAsync(organizationId, projectKey, http.RequestAborted);
            if (project is { IsArchived: true })
            {
                return Results.Problem(
                    title: "This project is archived.",
                    detail: $"{project.Key} is read-only until it is un-archived.",
                    type: ProblemTypes.ProjectArchived,
                    statusCode: StatusCodes.Status409Conflict);
            }

            var billing = http.RequestServices.GetRequiredService<IOrganizationBillingState>();
            if (await billing.IsReadOnlyAsync(organizationId, http.RequestAborted))
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

            return await next(context);
        });
}
