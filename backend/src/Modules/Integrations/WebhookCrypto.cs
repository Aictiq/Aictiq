using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Aictiq.Modules.Integrations;

public sealed class WebhookSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Aictiq.Integrations.WebhookSecret.v1");
    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    public static string CreateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

public static class WebhookSignature
{
    public static string Create(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
