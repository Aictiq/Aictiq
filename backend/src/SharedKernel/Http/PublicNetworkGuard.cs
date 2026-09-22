using System.Net;
using System.Net.Sockets;

namespace Aictiq.SharedKernel.Http;

/// <summary>
/// Keeps outbound HTTP the server makes on a user's behalf (link previews, webhooks) on
/// the public internet.
///
/// Checking the URL's host once, before the request, is not enough: the name is resolved
/// again when the socket opens, and a host that answers the pre-flight with a public
/// address and the connect with <c>10.0.0.1</c> (DNS rebinding) walks straight past the
/// check. So the same rule is applied <em>at connect time</em>, on the address the socket
/// is actually about to dial, through <see cref="SocketsHttpHandler.ConnectCallback"/>.
/// Redirects are never followed, because a redirect is just another destination that has
/// not been checked.
/// </summary>
public static class PublicNetworkGuard
{
    /// <summary>
    /// True for an address a server may be sent to by a user: not loopback, not link-local,
    /// not private (RFC 1918, RFC 6598), not multicast or reserved, on either IP version.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not 0 and not 10 and not 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)   // 100.64/10 shared address space
                && !(bytes[0] == 169 && bytes[1] == 254)                 // link-local, cloud metadata
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)  // 192.0.0/24 IETF protocol assignments
                && bytes[0] < 224;                                       // multicast and reserved
        }

        // ::/8 (includes ::, ::1 and the deprecated IPv4-compatible range), fc00::/7 (ULA),
        // fe80::/10 (link-local; also caught above), 64:ff9b::/96 is left public - it is
        // NAT64 to a public IPv4.
        return bytes[0] is not 0 and not 0xfc and not 0xfd && !(bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }

    /// <summary>
    /// Resolves a host and keeps only the addresses a request may be sent to. Empty means
    /// "do not connect". A literal address is checked without a lookup.
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return IsPublic(literal) ? [literal] : [];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            // All or nothing: a name that resolves to one public and one private address is
            // a name that can be steered, and the client cannot choose which one it dials.
            return addresses.Length > 0 && addresses.All(IsPublic) ? addresses : [];
        }
        catch (SocketException)
        {
            return [];
        }
    }

    /// <summary>
    /// A handler whose every connection is checked against <see cref="IsPublic"/> on the
    /// resolved address, immediately before the socket opens. With
    /// <paramref name="allowPrivateNetworks"/> the check is skipped - the self-hosted
    /// escape hatch for a webhook receiver on the same LAN - but redirects are still not
    /// followed.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks = false)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        if (!allowPrivateNetworks)
        {
            handler.ConnectCallback = ConnectToPublicAddressAsync;
        }

        return handler;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolvePublicAsync(endpoint.Host, cancellationToken);
        if (addresses.Length == 0)
        {
            throw new HttpRequestException(
                $"'{endpoint.Host}' does not resolve to a public internet address.");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException($"Could not connect to '{endpoint.Host}'.", last);
    }
}
