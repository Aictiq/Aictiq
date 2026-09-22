using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Integrations.Contracts;

/// <summary>Durable notification that a verified GitHub delivery is available in the inbox.</summary>
public sealed record GitHubDeliveryReceived(string DeliveryId) : DomainEvent, IIntegrationEvent;
