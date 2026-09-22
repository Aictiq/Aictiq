using System.Security.Claims;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.Api.Infrastructure;

// Claim types are the short JWT names ("sub", "name", "role", "org", "typ", "scp")
// because the bearer handler is configured with MapInboundClaims = false.
public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    // The accessor is an AsyncLocal, and not every caller runs on the request's execution
    // context: HybridCache runs a cache factory on a thread-pool item that does not flow it.
    // There the RLS session interceptor saw no user, a membership lookup read zero rows, and
    // "not a member" was cached for five minutes - 404 on every route in the organization.
    // This service is scoped, so every principal it observes on the request's own context
    // is that request's; off-context it answers with the last one it saw. It deliberately
    // does not keep the HttpContext itself: those are pooled, and a reference that outlived
    // the request could read the next request's user.
    private ClaimsPrincipal? _lastSeen = httpContextAccessor.HttpContext?.User;

    private ClaimsPrincipal? Principal
    {
        get
        {
            if (httpContextAccessor.HttpContext is { } context) _lastSeen = context.User;
            return _lastSeen;
        }
    }

    public string? UserId => Principal?.FindFirstValue("sub");

    public string? UserName => Principal?.FindFirstValue("name");

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll("role").Select(c => c.Value).ToArray() ?? [];

    public bool IsAgent =>
        string.Equals(
            Principal?.FindFirstValue(PrincipalClaims.PrincipalType),
            PrincipalClaims.AgentPrincipalType,
            StringComparison.Ordinal);

    public IReadOnlyCollection<string> Scopes =>
        Principal?.FindAll(PrincipalClaims.Scope).Select(c => c.Value).ToArray() ?? [];

    public Guid? OrganizationId =>
        Guid.TryParse(Principal?.FindFirstValue(PrincipalClaims.Organization), out var id) ? id : null;
}
