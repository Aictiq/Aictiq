namespace Aictiq.Modules.Integrations;

public sealed class WebhooksOptions
{
    public const string SectionName = "Webhooks";
    public bool AllowPrivateNetworks { get; set; }
    public int BatchSize { get; set; } = 25;
    public int MaxAttempts { get; set; } = 8;
    public int BaseRetrySeconds { get; set; } = 5;
    public int MaxRetrySeconds { get; set; } = 3600;
}
