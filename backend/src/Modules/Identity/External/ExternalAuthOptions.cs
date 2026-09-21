namespace Aictiq.Modules.Identity.External;

/// <summary>
/// OAuth sign-in providers, each optional.
///
/// Like SMTP, absent is a configuration rather than a fault: an instance with no provider
/// credentials is a supported deployment that simply offers password sign-in, so nothing
/// here is <c>[Required]</c> and nothing is validated on start. <c>GET /auth/providers</c>
/// is how the login page learns which buttons to draw.
/// </summary>
public sealed class ExternalAuthOptions
{
    public const string SectionName = "Auth";

    public ExternalProviderOptions? Google { get; set; }

    public ExternalProviderOptions? GitHub { get; set; }

    public IEnumerable<(string Name, ExternalProviderOptions Options)> Configured()
    {
        if (Google is { IsConfigured: true }) yield return (ExternalProviders.Google, Google);
        if (GitHub is { IsConfigured: true }) yield return (ExternalProviders.GitHub, GitHub);
    }
}

public sealed class ExternalProviderOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Scheme names. They are also what a client sends in the URL, so they are stable.</summary>
public static class ExternalProviders
{
    public const string Google = "google";
    public const string GitHub = "github";

    /// <summary>
    /// The cookie the OAuth handlers sign into, and the only thing it is for. It lives for
    /// the seconds between the provider's redirect and our callback reading it; it is not
    /// a session, and the callback signs it out the moment it has been read.
    /// </summary>
    public const string ExternalScheme = "Aictiq.External";

    public static bool IsKnown(string provider) =>
        provider is Google or GitHub;
}
