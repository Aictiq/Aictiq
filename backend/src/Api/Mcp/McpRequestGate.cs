using System.Diagnostics;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using ModelContextProtocol;

namespace Aictiq.Api.Mcp;

/// <summary>
/// The checks every MCP request that reaches data must pass, whatever primitive it names.
///
/// Tools and resources are two doors to the same rows: <c>aictiq://item/{key}</c> returns
/// what <c>get_item</c> returns. A gate that guarded only tool calls would let a token
/// without the <c>read</c> scope - or one whose identity has left its organization - read
/// through the other door, and without the rate limit or an audit row.
/// </summary>
public static class McpRequestGate
{
    /// <summary>
    /// Membership, scope and rate limit, in that order. Throws the refusal an MCP client
    /// sees; returns normally when the request may run.
    /// </summary>
    /// <param name="requiredScope">Null only for the <c>whoami</c> diagnostic.</param>
    public static async Task AuthorizeAsync(IServiceProvider services, string? requiredScope, CancellationToken cancellationToken)
    {
        var user = services.GetRequiredService<ICurrentUser>();
        var tenant = services.GetRequiredService<ICurrentTenant>();
        var access = services.GetRequiredService<IProjectAccess>();
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("MCP requests require an HTTP context.");

        // A PAT can outlive organization membership. The route-less /mcp endpoint does not
        // get TenantResolutionMiddleware's slug check, so enforce the same fail-closed
        // membership rule here before a module can read or write anything. whoami is
        // deliberately exempt: it is the diagnostic an off-boarded agent needs to stop.
        if (requiredScope is not null
            && (user.UserId is not { } userId || tenant.OrganizationId is not { } organizationId
                || await access.GetOrgRoleAsync(userId, organizationId, cancellationToken) is null))
        {
            throw new McpException("This token's identity is not a member of its bound organization.");
        }

        if (requiredScope is not null && !ScopeRequirements.IsSatisfiedBy(user.Scopes, requiredScope))
        {
            throw new McpException($"This request requires the '{requiredScope}' token scope.");
        }

        var retryAfter = services.GetRequiredService<McpToolRateLimiter>().RetryAfterSeconds(http.Request);
        if (retryAfter is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new McpException($"Rate limit exceeded. Retry-After: {seconds} seconds.");
        }
    }

    public static async Task RecordAsync(IServiceProvider services, string name, string outcome, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        try
        {
            await services.GetRequiredService<McpToolAuditService>().RecordAsync(name, outcome, stopwatch, cancellationToken);
        }
        catch
        {
            // A completed request must not become a failed one solely because an audit
            // write is temporarily unavailable; the request itself is still traced.
        }
    }
}
