using System.Security.Cryptography;
using System.Text;

namespace Aictiq.Modules.Integrations;

/// <summary>
/// Short-lived, signed state passed through GitHub's installation redirect. It names an
/// organization but does not authenticate the caller; the callback independently checks
/// that the signed-in user is an organization administrator.
/// </summary>
public static class GitHubInstallState
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static string Create(Guid organizationId, string secret, TimeProvider clock)
    {
        var expires = clock.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds();
        var payload = $"{organizationId:N}.{expires}";
        var encoded = Base64Url(Encoding.UTF8.GetBytes(payload));
        return $"{encoded}.{Sign(encoded, secret)}";
    }

    public static bool TryValidate(string? state, string secret, TimeProvider clock, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        var parts = state?.Split('.', 2);
        if (parts is not [var payload, var signature] || payload.Length == 0 || signature.Length != 43)
        {
            return false;
        }

        byte[] supplied;
        byte[] expected;
        try
        {
            supplied = Base64UrlDecode(signature);
            expected = Base64UrlDecode(Sign(payload, secret));
        }
        catch (FormatException)
        {
            return false;
        }

        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            return false;
        }

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Base64UrlDecode(payload)); }
        catch (FormatException) { return false; }
        var fields = decoded.Split('.', 2);
        return fields is [var id, var expiry]
            && Guid.TryParseExact(id, "N", out organizationId)
            && long.TryParse(expiry, out var expires)
            && clock.GetUtcNow().ToUnixTimeSeconds() <= expires;
    }

    private static string Sign(string value, string secret) =>
        Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
