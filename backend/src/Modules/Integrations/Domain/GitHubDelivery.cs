namespace Aictiq.Modules.Integrations.Domain;

/// <summary>
/// Immutable GitHub webhook inbox row. DeliveryId is GitHub's UUID and the database
/// primary key, so a redelivery cannot schedule a second downstream event.
/// </summary>
public sealed class GitHubDelivery
{
    public required string DeliveryId { get; init; }
    public long? InstallationId { get; init; }
    public Guid? OrganizationId { get; init; }
    public required string EventType { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
