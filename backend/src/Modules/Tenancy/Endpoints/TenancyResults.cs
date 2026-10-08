using Microsoft.AspNetCore.Http;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Contracts;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <summary>
/// The two refusals every tenancy endpoint makes, worded identically wherever they are
/// made. The distinction is the repository-wide rule: <b>404 for what you cannot see, 403
/// only for what you can</b> - so these are not interchangeable and neither is a matter of
/// taste at the call site.
/// </summary>
internal static class TenancyResults
{
    /// <summary>Says nothing about whether the thing exists - that is the point.</summary>
    public static IResult NotFound() =>
        Results.Problem(
            title: "Not found.",
            detail: "The record does not exist, or you do not have access to it.",
            type: ProblemTypes.NotAMember,
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>Only once the caller can already see what they are being refused.</summary>
    public static IResult Forbidden(string detail) =>
        Results.Problem(
            title: "Insufficient permissions.",
            detail: detail,
            type: ProblemTypes.InsufficientRole,
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// 402 plan-limit: the stable <c>limit</c> name and, when paying lifts it (the hosted
    /// free tier), where to go to pay.
    /// </summary>
    public static IResult PlanLimited(PlanLimitDecision decision) =>
        Results.Problem(
            title: "Plan limit reached.",
            detail: decision.Reason ?? "This organization cannot add another member on its current plan.",
            type: ProblemTypes.PlanLimit,
            statusCode: StatusCodes.Status402PaymentRequired,
            extensions: new Dictionary<string, object?> { ["limit"] = decision.Limit, ["upgradeUrl"] = decision.UpgradeUrl });
}
