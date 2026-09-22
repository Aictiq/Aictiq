using Aictiq.SharedKernel.Domain;

namespace Aictiq.SharedKernel.Contracts;

/// <summary>
/// Wiki pages were deleted for good. Raised by Wiki; WorkItems owns the attachments a page
/// may have and removes them. It lives in SharedKernel because WorkItems may not reference
/// the Wiki module's types. Deleting what is already gone is success, so a replay is harmless.
/// </summary>
public sealed record WikiPagesDeleted(Guid OrganizationId, Guid ProjectId, IReadOnlyList<Guid> PageIds)
    : DomainEvent, IIntegrationEvent;
