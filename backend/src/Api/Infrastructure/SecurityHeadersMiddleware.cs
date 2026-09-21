using Aictiq.SharedKernel.Http;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// The security headers on every response. The API used to omit CSP because a separate
/// Nuxt BFF owned the HTML; now the API serves the SPA itself (wwwroot + SPA fallback),
/// so the policy lives here.
/// </summary>
public static class SecurityHeadersMiddleware
{
    private static readonly string[] BaseDirectives =
    [
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        // Belt and braces with X-Frame-Options, which older browsers use instead.
        "frame-ancestors 'none'",
        "form-action 'self'",
        // Vue scoped styles and Reka UI's positioning arrive as inline style attributes.
        // Removing this needs nonce plumbing through the SPA's index.html.
        "style-src 'self' 'unsafe-inline'",
        "img-src 'self' data: blob:",
        "font-src 'self' data:",
    ];

    /// <summary>
    /// The SPA is same-origin with the API, so <c>connect-src 'self'</c> covers every
    /// call the browser makes — including the SignalR hub and presigned S3 URLs, which
    /// are reverse-proxied under this origin.
    /// </summary>
    public static readonly string ProductionCsp = string.Join(
        "; ",
        [.. BaseDirectives, "script-src 'self'", "connect-src 'self'", "upgrade-insecure-requests"]);

    /// <summary>
    /// Vite's dev client evaluates modules and opens an HMR websocket. The relaxation is
    /// confined here so production is never weakened to make the dev server work.
    /// </summary>
    public static readonly string DevelopmentCsp = string.Join(
        "; ",
        [.. BaseDirectives, "script-src 'self' 'unsafe-inline' 'unsafe-eval'", "connect-src 'self' ws: wss:"]);

    /// <summary>
    /// Adds the baseline headers to every response and CSP to HTML responses only —
    /// a CSP on a JSON body is dead weight, and API clients are not browsers.
    /// <c>Cache-Control: no-store</c> is likewise for API routes: the SPA's hashed
    /// assets must stay cacheable. An endpoint escapes it only by declaring
    /// <see cref="CacheableResponseAttribute"/>.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool isDevelopment) =>
        app.Use(async (context, next) =>
        {
            var csp = isDevelopment ? DevelopmentCsp : ProductionCsp;

            context.Response.OnStarting(state =>
            {
                var ctx = (HttpContext)state;
                var headers = ctx.Response.Headers;

                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] =
                    "camera=(), microphone=(), geolocation=(), interest-cohort=()";

                // Everything the API serves is private JSON and must not be cached —
                // except the handful of routes that say otherwise in writing, which set
                // their own header and carry the marker so this cannot clobber it.
                if (IsBackendRoute(ctx.Request.Path) && !IsDeliberatelyCacheable(ctx))
                {
                    headers["Cache-Control"] = "no-store";
                }
                else if (IsHtml(ctx.Response.ContentType))
                {
                    headers["Content-Security-Policy"] = csp;
                    // index.html names this deploy's hashed assets, so it must be
                    // revalidated; the assets themselves stay cacheable forever.
                    headers["Cache-Control"] = "no-cache";
                }

                return Task.CompletedTask;
            }, context);

            await next();
        });

    /// <summary>
    /// The prefixes the API owns. Everything else is the SPA, and falls back to
    /// <c>index.html</c>. Program.cs reuses this list so the SPA fallback and the
    /// no-store rule can never drift apart.
    /// </summary>
    public static readonly string[] BackendPrefixes =
        ["/api", "/mcp", "/hubs", "/health", "/openapi", "/scalar"];

    public static bool IsBackendRoute(PathString path) =>
        Array.Exists(BackendPrefixes, prefix => path.StartsWithSegments(prefix));

    private static bool IsDeliberatelyCacheable(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<CacheableResponseAttribute>() is not null;

    private static bool IsHtml(string? contentType) =>
        contentType is not null
        && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}
