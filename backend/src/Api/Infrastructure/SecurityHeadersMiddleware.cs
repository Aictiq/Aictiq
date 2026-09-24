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

    private static readonly string[] ProductionDirectives =
        [.. BaseDirectives, "script-src 'self'", "connect-src 'self'"];

    /// <summary>
    /// The SPA is same-origin with the API, so <c>connect-src 'self'</c> covers every
    /// call the browser makes - including the SignalR hub and presigned S3 URLs, which
    /// are reverse-proxied under this origin.
    /// </summary>
    public static readonly string ProductionCsp = string.Join(
        "; ",
        [.. ProductionDirectives, "upgrade-insecure-requests"]);

    /// <summary>
    /// The same policy without <c>upgrade-insecure-requests</c>, for an instance served
    /// over plain HTTP - an evaluation or an internal network, which AICTIQ_URL supports
    /// explicitly. The directive would otherwise rewrite every same-origin asset request
    /// to https on a host that has no listener there, and the SPA would never load.
    /// Browsers exempt localhost from the upgrade, which is why only a real hostname or
    /// address shows it.
    /// </summary>
    public static readonly string ProductionCspWithoutUpgrade = string.Join("; ", ProductionDirectives);

    /// <summary>
    /// Vite's dev client evaluates modules and opens an HMR websocket. The relaxation is
    /// confined here so production is never weakened to make the dev server work.
    /// </summary>
    public static readonly string DevelopmentCsp = string.Join(
        "; ",
        [.. BaseDirectives, "script-src 'self' 'unsafe-inline' 'unsafe-eval'", "connect-src 'self' ws: wss:"]);

    /// <summary>
    /// Cloudflare Turnstile loads its script from this origin and renders the challenge in
    /// an iframe from it. Allowed only on an instance that has Turnstile configured, so a
    /// deployment that does not use it keeps a policy with no third party in it at all.
    /// </summary>
    public const string TurnstileOrigin = "https://challenges.cloudflare.com";

    /// <summary>
    /// The policy with Turnstile's origin let in: appended to <c>script-src</c>, and a
    /// <c>frame-src</c> added, which otherwise falls back to <c>default-src 'self'</c>.
    /// </summary>
    public static string WithTurnstile(string policy) =>
        string.Join("; ", policy.Split("; ")
            .Select(directive => directive.StartsWith("script-src ", StringComparison.Ordinal)
                ? $"{directive} {TurnstileOrigin}"
                : directive)
            .Append($"frame-src 'self' {TurnstileOrigin}"));

    /// <summary>
    /// Adds the baseline headers to every response and CSP to HTML responses only -
    /// a CSP on a JSON body is dead weight, and API clients are not browsers.
    /// <c>Cache-Control: no-store</c> is likewise for API routes: the SPA's hashed
    /// assets must stay cacheable. An endpoint escapes it only by declaring
    /// <see cref="CacheableResponseAttribute"/>.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(
        this IApplicationBuilder app, bool isDevelopment, bool turnstileEnabled = false)
    {
        // Built once: the configuration that decides them cannot change while running.
        var development = turnstileEnabled ? WithTurnstile(DevelopmentCsp) : DevelopmentCsp;
        var https = turnstileEnabled ? WithTurnstile(ProductionCsp) : ProductionCsp;
        var plain = turnstileEnabled ? WithTurnstile(ProductionCspWithoutUpgrade) : ProductionCspWithoutUpgrade;

        return app.Use(async (context, next) =>
        {
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

                // Everything the API serves is private JSON and must not be cached -
                // except the handful of routes that say otherwise in writing, which set
                // their own header and carry the marker so this cannot clobber it.
                if (IsBackendRoute(ctx.Request.Path) && !IsDeliberatelyCacheable(ctx))
                {
                    headers["Cache-Control"] = "no-store";
                }
                else if (IsHtml(ctx.Response.ContentType))
                {
                    // Read after UseForwardedHeaders has run, so X-Forwarded-Proto from
                    // the reverse proxy decides this and not the plain hop to Kestrel.
                    headers["Content-Security-Policy"] = isDevelopment
                        ? development
                        : ctx.Request.IsHttps ? https : plain;
                    // index.html names this deploy's hashed assets, so it must be
                    // revalidated; the assets themselves stay cacheable forever.
                    headers["Cache-Control"] = "no-cache";
                }

                return Task.CompletedTask;
            }, context);

            await next();
        });
    }

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
