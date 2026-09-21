using System.Reflection;
using System.Text.RegularExpressions;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// The small, public release contract consumed by the web footer and installation
/// tooling. It deliberately has no tenant or authentication dependency.
/// </summary>
public sealed record ReleaseMetadata(string Version, UpdateCheckMetadata UpdateCheck)
{
    private static readonly Regex GitHubRepository = new(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static ReleaseMetadata From(IConfiguration configuration)
    {
        // Version is normally supplied by the assembly (and by release CI through
        // -p:Version). The configuration override is useful to image builders that
        // stamp a version outside MSBuild, without making a git command a runtime
        // dependency.
        var version = configuration["Release:Version"];
        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(ReleaseMetadata).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ReleaseMetadata).Assembly.GetName().Version?.ToString()
                ?? "0.0.0-unknown";
        }

        // SourceRevisionId is commonly appended as SemVer build metadata. It is useful
        // in an artifact but noisy in a footer, and has no bearing on version ordering.
        version = version.Split('+', 2)[0];

        var updatesEnabled = configuration.GetValue("Release:UpdateCheck:Enabled", false);
        var repository = configuration["Release:UpdateCheck:Repository"]?.Trim();
        if (!updatesEnabled || string.IsNullOrEmpty(repository) || !GitHubRepository.IsMatch(repository))
        {
            return new ReleaseMetadata(version, new UpdateCheckMetadata(false, null));
        }

        return new ReleaseMetadata(version, new UpdateCheckMetadata(true, repository));
    }
}

/// <summary>
/// Checking GitHub is opt-in: self-hosted installations otherwise make no release
/// lookup and do not disclose their browser's IP address to GitHub.
/// </summary>
public sealed record UpdateCheckMetadata(bool Enabled, string? Repository);
