using System.Security.Cryptography;
using System.Text;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.AspNetCore.DataProtection;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// Webhook URLs and chat ids are credentials: whoever holds a Slack webhook URL can post to
/// that channel. They are stored encrypted, as <c>WebhookSecretProtector</c> does for
/// outgoing webhook secrets, and only <see cref="Mask"/>ed forms ever leave this class.
/// </summary>
public sealed class ChatSecrets(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Aictiq.Notifications.ChatTarget.v1");

    public string Protect(string target) => _protector.Protect(target);
    public string Unprotect(string protectedTarget) => _protector.Unprotect(protectedTarget);

    /// <summary>Ten characters a person can type: no 0/O or 1/I to confuse.</summary>
    public static string CreateCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(10, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()))).ToLowerInvariant();

    /// <summary>Enough to recognize which webhook it is, not enough to post to it.</summary>
    public static string Mask(ChatChannelType type, string target)
    {
        var tail = target.Length > 4 ? target[^4..] : "";
        return type switch
        {
            ChatChannelType.Slack => $"hooks.slack.com/…{tail}",
            ChatChannelType.Discord => $"discord.com/api/webhooks/…{tail}",
            _ => $"••••{tail}"
        };
    }

    /// <summary>
    /// Only the platforms' own webhook hosts are accepted. Besides catching a pasted
    /// wrong URL, it means a user-supplied address can never point the server at an
    /// internal service.
    /// </summary>
    public static string? ValidateWebhookUrl(ChatChannelType type, string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
            return "Enter the https webhook URL the platform gave you.";
        return type switch
        {
            ChatChannelType.Slack when uri.Host == "hooks.slack.com" && uri.AbsolutePath.StartsWith("/services/", StringComparison.Ordinal) => null,
            ChatChannelType.Slack => "A Slack webhook URL starts with https://hooks.slack.com/services/.",
            ChatChannelType.Discord when uri.Host is "discord.com" or "discordapp.com" or "canary.discord.com" or "ptb.discord.com"
                && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal) => null,
            ChatChannelType.Discord => "A Discord webhook URL starts with https://discord.com/api/webhooks/.",
            _ => "Telegram is connected with a code, not a URL."
        };
    }
}
