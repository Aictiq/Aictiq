namespace Aictiq.SharedKernel.Tenancy;

/// <summary>
/// The organization every query and write in the current unit of work belongs to.
///
/// This is the spine of tenant isolation: module contexts filter every tenant table on
/// it, and refuse to write a tenant row without it. Null means "no tenant established" —
/// which reads as *nothing is visible* rather than *everything is*, so a code path that
/// forgets to set it fails closed.
/// </summary>
public interface ICurrentTenant
{
    Guid? OrganizationId { get; }
}

/// <summary>
/// The scoped, settable implementation. The API's tenant middleware sets it once per
/// request; background work opens a scope per message with <see cref="Use"/>.
/// </summary>
public sealed class AmbientCurrentTenant : ICurrentTenant
{
    public Guid? OrganizationId { get; private set; }

    // Invitation redemption starts without an organization route. The opaque token hash
    // is scoped only long enough for its RLS-protected lookup.
    public string? InvitationTokenHash { get; private set; }

    // A runner credential is the same shape of problem: its lookup is what decides
    // the tenant, so the hash is a capability that RLS admits for exactly that row.
    public string? RunnerTokenHash { get; private set; }

    public void Set(Guid? organizationId) => OrganizationId = organizationId;

    /// <summary>
    /// Runs a block against one organization and restores the previous tenant afterwards.
    /// Workers process messages for many tenants on one scope, so restoring matters:
    /// leaking a tenant across messages would silently widen every later query.
    /// </summary>
    public IDisposable Use(Guid organizationId)
    {
        var previous = OrganizationId;
        OrganizationId = organizationId;
        return new Restore(this, previous);
    }

    public IDisposable UseInvitationTokenHash(string tokenHash)
    {
        var previous = InvitationTokenHash;
        InvitationTokenHash = tokenHash;
        return new RestoreInvitationTokenHash(this, previous);
    }

    public IDisposable UseRunnerTokenHash(string tokenHash)
    {
        var previous = RunnerTokenHash;
        RunnerTokenHash = tokenHash;
        return new RestoreRunnerTokenHash(this, previous);
    }

    private sealed class RestoreRunnerTokenHash(AmbientCurrentTenant tenant, string? previous) : IDisposable
    {
        public void Dispose() => tenant.RunnerTokenHash = previous;
    }

    private sealed class Restore(AmbientCurrentTenant tenant, Guid? previous) : IDisposable
    {
        public void Dispose() => tenant.OrganizationId = previous;
    }

    private sealed class RestoreInvitationTokenHash(AmbientCurrentTenant tenant, string? previous) : IDisposable
    {
        public void Dispose() => tenant.InvitationTokenHash = previous;
    }
}
