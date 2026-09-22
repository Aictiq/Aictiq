using System.Security.Cryptography;
using System.Text;

namespace Aictiq.Modules.Integrations;

/// <summary>GitHub's <c>X-Hub-Signature-256</c> verifier with a fixed-time comparison.</summary>
public static class GitHubWebhookSignature
{
    public static bool IsValid(string? signature, ReadOnlySpan<byte> payload, string? secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrEmpty(secret)
            || !signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = signature[7..];
        if (hex.Length != 64)
        {
            return false;
        }

        byte[] supplied;
        try { supplied = Convert.FromHexString(hex); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
