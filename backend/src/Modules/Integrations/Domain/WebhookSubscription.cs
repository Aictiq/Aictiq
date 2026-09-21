using Aictiq.SharedKernel.Domain;

namespace Aictiq.Modules.Integrations.Domain;

/// <summary>An organization- or project-scoped destination for Aictiq domain events.</summary>
public sealed class WebhookSubscription : TenantEntity, IAudited
{
    public Guid? ProjectId { get; set; }
    public required string Url { get; set; }
    /// <summary>SHA-256 of the generated secret; useful for audit/support without exposing it.</summary>
    public required string SecretHash { get; init; }
    /// <summary>Data-protected secret used solely by the delivery worker to sign the body.</summary>
    public required string SecretProtected { get; init; }
    public string[] Events { get; set; } = [];
    public bool Active { get; set; } = true;
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One queued outgoing attempt. A delivery id remains stable across retries.</summary>
public sealed class WebhookDelivery : TenantEntity
{
    public Guid SubscriptionId { get; init; }
    public Guid EventId { get; init; }
    public required string EventName { get; init; }
    public required string Payload { get; init; }
    public int Attempt { get; set; }
    public string Status { get; set; } = "pending";
    public int? StatusCode { get; set; }
    public string? ResponseExcerpt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
}
