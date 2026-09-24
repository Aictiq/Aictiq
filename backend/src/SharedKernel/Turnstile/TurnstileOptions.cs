namespace Aictiq.SharedKernel.Turnstile;

/// <summary>
/// Cloudflare Turnstile, bound from the <c>Turnstile</c> section.
///
/// Optional, like email: a development machine, a test suite or a self-hosted instance
/// on a private network has no bots to keep out, and the whole section being absent is
/// the "no challenge" configuration. A public deployment sets both keys, and from then on
/// the anonymous credential endpoints refuse a request the widget did not vouch for.
///
/// Half a configuration is refused at start-up (<see cref="TurnstileOptionsValidator"/>):
/// a site key without its secret would render a widget whose answer nobody checks, and a
/// secret without a site key would refuse every sign-in because the page never shows one.
/// </summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>The public half. Sent to the browser, which renders the widget with it.</summary>
    public string? SiteKey { get; set; }

    /// <summary>The private half. Only ever sent to Cloudflare's siteverify endpoint.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Host names a solved challenge may come from, compared with the <c>hostname</c>
    /// siteverify reports. Empty accepts any host the widget itself is allowed on - which
    /// the widget's hostname list in the Cloudflare dashboard already restricts, so this is
    /// belt and braces for a site key shared between several deployments.
    /// </summary>
    public string[] AllowedHostnames { get; set; } = [];

    /// <summary>Overridable for tests; there is no other reason to change it.</summary>
    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public int TimeoutSeconds { get; set; } = 10;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
