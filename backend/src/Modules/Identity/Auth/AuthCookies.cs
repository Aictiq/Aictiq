using Microsoft.AspNetCore.Http;

namespace Aictiq.Modules.Identity.Auth;

/// <summary>
/// The browser half of authentication. The SPA is same-origin with the API, so the API
/// sets the tokens as httpOnly cookies itself and JavaScript never sees a credential —
/// there is nothing in <c>document.cookie</c> to steal and nothing to put in storage.
///
/// Non-browser clients (CLI, MCP, scripts) keep the bearer path: they ask for
/// <c>?mode=body</c> (the default when the SPA's request header is absent) and carry the
/// tokens themselves.
/// </summary>
public static class AuthCookies
{
    /// <summary>Access JWT. Path <c>/</c> so every API call carries it.</summary>
    public const string AccessCookieName = "aictiq.at";

    /// <summary>
    /// Refresh token. Scoped to the refresh endpoint, so it is not attached to — and
    /// cannot leak from — any other request the app makes.
    /// </summary>
    public const string RefreshCookieName = "aictiq.rt";

    public const string RefreshCookiePath = "/api/v1/auth/refresh";

    /// <summary>
    /// The CSRF guard for cookie authentication. A cross-site form post cannot set a
    /// custom header, and the header is not itself a credential, so requiring it on
    /// state-changing requests is enough — no token round-trip needed.
    /// </summary>
    public const string RequestHeaderName = "X-Aictiq-Request";

    /// <summary>
    /// Set on <see cref="HttpContext.Items"/> when the principal was authenticated from
    /// the cookie rather than an <c>Authorization</c> header. Only those requests need
    /// the CSRF header: a bearer token is never attached by the browser automatically.
    /// </summary>
    public const string CookieAuthenticatedItemKey = "aictiq.auth.cookie";

    /// <summary>
    /// Cookie mode is the default for the SPA and only the SPA: it is the one client
    /// that sends <see cref="RequestHeaderName"/>. An explicit <c>?mode=</c> wins, so a
    /// browser-based tool can still ask for the body.
    /// </summary>
    public static bool UseCookies(HttpRequest request, string? mode) => mode switch
    {
        "cookie" => true,
        "body" => false,
        _ => request.Headers.ContainsKey(RequestHeaderName),
    };

    public static bool HasRequestHeader(HttpRequest request) =>
        request.Headers.TryGetValue(RequestHeaderName, out var value) && value.Count > 0;

    public static void Issue(HttpContext context, TokenPair tokens, JwtOptions options)
    {
        var secure = IsHttps(context.Request);
        var now = DateTimeOffset.UtcNow;

        context.Response.Cookies.Append(AccessCookieName, tokens.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            // Lax, not Strict: a top-level navigation into the app from an external link
            // must arrive authenticated, or every shared item link bounces through login.
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = tokens.AccessTokenExpiresAt,
        });

        context.Response.Cookies.Append(RefreshCookieName, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            // Strict is free here: the refresh cookie is only ever sent by the app's own
            // fetch to its own origin, never on a navigation.
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = now.AddDays(options.RefreshTokenDays),
        });
    }

    public static void Clear(HttpContext context)
    {
        var secure = IsHttps(context.Request);

        // Deleting a cookie only works when the attributes match the ones it was set
        // with, so path and SameSite are repeated here.
        context.Response.Cookies.Delete(AccessCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

        context.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });
    }

    /// <summary>
    /// Behind a proxy that is not in <c>ReverseProxy:KnownProxies</c>, ASP.NET will not
    /// rewrite the scheme, so the forwarded header is consulted directly. Trusting it
    /// can only make the cookie <em>more</em> restrictive — the failure mode of a spoofed
    /// header is a cookie the spoofer's own browser then refuses to send back.
    /// </summary>
    private static bool IsHttps(HttpRequest request) =>
        request.IsHttps
        || string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
}
