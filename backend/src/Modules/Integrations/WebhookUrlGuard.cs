using System.Net;
using Aictiq.SharedKernel.Http;

namespace Aictiq.Modules.Integrations;

/// <summary>
/// Rejects destinations that could turn the worker into an internal-network proxy. The
/// pre-flight here gives the person a readable error; the connect-time check in
/// <see cref="PublicNetworkGuard"/> is what actually holds when the name is rebound later.
/// </summary>
public static class WebhookUrlGuard
{
    public static async Task<string?> ValidateAsync(string? url, bool allowPrivateNetworks, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return "Webhook URLs must be absolute HTTPS URLs.";
        if (allowPrivateNetworks) return null;
        return (await PublicNetworkGuard.ResolvePublicAsync(uri.DnsSafeHost, cancellationToken)).Length == 0
            ? "Webhook URLs cannot resolve to a private or local network."
            : null;
    }

    public static bool IsPrivate(IPAddress address) => !PublicNetworkGuard.IsPublic(address);
}
