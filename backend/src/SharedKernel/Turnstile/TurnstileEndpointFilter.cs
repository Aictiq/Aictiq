using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.SharedKernel.Turnstile;

/// <summary>
/// Puts an endpoint behind a Turnstile challenge: <c>.RequireTurnstile("login")</c>.
///
/// The token travels in a header rather than in each request body, so one filter covers
/// every form and no request record grows a field that means nothing to the CLI or to a
/// test. It runs before the handler, so a bot that fails it never reaches the password
/// hasher, the user table or the mail outbox. With Turnstile unconfigured the filter
/// passes everything through, which is what development and the test suite rely on.
/// </summary>
public static class TurnstileEndpointFilter
{
    public const string HeaderName = "X-Turnstile-Token";

    public static TBuilder RequireTurnstile<TBuilder>(this TBuilder builder, string action)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var verifier = http.RequestServices.GetRequiredService<ITurnstileVerifier>();
            if (!verifier.IsEnabled)
            {
                return await next(context);
            }

            var outcome = await verifier.VerifyAsync(
                http.Request.Headers[HeaderName].ToString(), action, http.RequestAborted);

            return outcome switch
            {
                TurnstileOutcome.Passed => await next(context),
                TurnstileOutcome.Unavailable => Results.Problem(
                    title: "The verification check is unavailable.",
                    detail: "Nothing was submitted. Wait a moment and try again.",
                    type: ProblemTypes.ChallengeFailed,
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(
                    title: "Complete the verification check and try again.",
                    type: ProblemTypes.ChallengeFailed,
                    statusCode: StatusCodes.Status400BadRequest),
            };
        });
}
