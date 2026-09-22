using Aictiq.Modules.Identity.Auth;
using Aictiq.SharedKernel;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// CSRF protection for cookie authentication.
///
/// The browser attaches <c>aictiq.at</c> to any request to this origin, including one a
/// third-party page triggers. SameSite=Lax blocks the cross-site cases that matter for
/// state changes, but it is a browser default, not a guarantee - so the API requires a
/// custom header as well. A cross-site form post or image cannot set one, and anything
/// that can (fetch, XHR) is already gated by the same-origin policy.
///
/// Bearer requests are exempt on purpose: nothing attaches an <c>Authorization</c> header
/// automatically, so a CLI or MCP client forging one would have to already hold the token.
/// </summary>
public static class CookieCsrfMiddleware
{
    public static IApplicationBuilder UseCookieCsrfProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Items.ContainsKey(AuthCookies.CookieAuthenticatedItemKey)
                && !IsSafeMethod(context.Request.Method)
                && !AuthCookies.HasRequestHeader(context.Request))
            {
                await Results.Problem(
                        title: $"Missing {AuthCookies.RequestHeaderName} header.",
                        detail: "Cookie-authenticated requests that change state must send "
                            + $"{AuthCookies.RequestHeaderName}: 1.",
                        type: ProblemTypes.CsrfHeaderMissing,
                        statusCode: StatusCodes.Status403Forbidden)
                    .ExecuteAsync(context);
                return;
            }

            await next();
        });

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}
