namespace Aictiq.Modules.Integrations;

/// <summary>
/// Configuration for a GitHub App. All App credentials are optional at the host level:
/// an installation without them simply has no GitHub routes, rather than a half-working
/// integration visible to users.
/// </summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public string? AppId { get; init; }
    public string? AppSlug { get; init; }
    public string? PrivateKey { get; init; }
    public string? WebhookSecret { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public bool IsConfigured => long.TryParse(AppId, out _)
        && !string.IsNullOrWhiteSpace(AppSlug)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(WebhookSecret);
}
