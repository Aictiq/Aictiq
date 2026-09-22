namespace Aictiq.SharedKernel.Tenancy;

/// <summary>
/// Thrown when a tenant row would be written with no organization established, or with a
/// different one than the current scope.
///
/// It is deliberately an exception and not a silent skip: a tenant row without a tenant
/// is unreachable data at best and a cross-tenant leak at worst, and either is a bug in
/// the calling code that must surface loudly in development rather than in an incident.
/// </summary>
public sealed class TenantMissingException(string message) : InvalidOperationException(message)
{
    public static TenantMissingException NoTenant(string entityType) =>
        new($"Cannot save {entityType}: it is tenant-scoped but no organization is in scope. "
            + "Set ICurrentTenant (the API's tenant middleware does this per request; background "
            + "work should open a scope with AmbientCurrentTenant.Use).");

    public static TenantMissingException WrongTenant(string entityType, Guid entityOrg, Guid currentOrg) =>
        new($"Cannot save {entityType} for organization {entityOrg} while organization {currentOrg} "
            + "is in scope - a write must never cross a tenant boundary.");
}
