using Microsoft.AspNetCore.Http;
using Aictiq.SharedKernel;

namespace Aictiq.Modules.Tenancy.Endpoints;

/// <summary>
/// The two refusals every tenancy endpoint makes, worded identically wherever they are
/// made. The distinction is the repository-wide rule: <b>404 for what you cannot see, 403
/// only for what you can</b> — so these are not interchangeable and neither is a matter of
/// taste at the call site.
/// </summary>
internal static class TenancyResults
{
    /// <summary>Says nothing about whether the thing exists — that is the point.</summary>
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
}
