using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.WorkItems.Domain;

/// <summary>
/// A project-scoped tag. <see cref="Group"/> is free text (e.g. "type") so a chip can read
/// "type: frontend" — namespacing without a second table.
/// </summary>
public sealed class Label : TenantEntity
{
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public string? Color { get; set; }
    public string? Description { get; set; }
    public string? Group { get; set; }
    public uint Version { get; private set; }
}

/// <summary>
/// One item wearing one label. Keyed by the pair rather than a surrogate id, like
/// <see cref="ProjectSequence"/> — there is nothing else this row needs to be found by.
/// Both foreign keys cascade so deleting a label removes it from every item, and deleting
/// an item removes its labels, as a database guarantee rather than endpoint cleanup.
/// </summary>
public sealed class ItemLabel : TenantEntity
{
    public Guid ItemId { get; init; }
    public Guid LabelId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public required string AddedBy { get; init; }
}
