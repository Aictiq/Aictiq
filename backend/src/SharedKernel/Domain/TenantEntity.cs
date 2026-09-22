namespace Aictiq.SharedKernel.Domain;

/// <summary>
/// Base for every row that belongs to one organization.
///
/// Deriving from this is the whole opt-in: module contexts find these by type and apply
/// the organization query filter automatically, so a new tenant table cannot be added
/// *without* isolation by forgetting a `.HasQueryFilter(...)` call. Composite uniqueness
/// on such a table must always include <see cref="OrganizationId"/>.
/// </summary>
public abstract class TenantEntity : EntityBase
{
    public Guid OrganizationId { get; set; }
}
