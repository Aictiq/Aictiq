using System.Text;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Aictiq.SharedKernel.Email;

namespace Aictiq.Modules.Notifications.Delivery;

public sealed class NotificationUnsubscribeTokens(IDataProtectionProvider protection, IOptions<EmailOptions> email)
{
    private readonly IDataProtector _protector = protection.CreateProtector("aictiq.notifications.unsubscribe.v1");

    public string Create(string userId, NotificationKind kind)
    {
        var payload = $"{userId}\n{(short)kind}";
        var token = _protector.Protect(payload);
        var root = email.Value.BaseUrl?.TrimEnd('/') ?? "";
        return $"{root}/api/v1/email/unsubscribe?token={Uri.EscapeDataString(token)}";
    }

    public bool TryRead(string? token, out string userId, out NotificationKind kind)
    {
        userId = ""; kind = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var parts = _protector.Unprotect(token).Split('\n', 2);
            if (parts.Length != 2 || !short.TryParse(parts[1], out var value) ||
                !Enum.IsDefined(typeof(NotificationKind), value) || string.IsNullOrWhiteSpace(parts[0])) return false;
            userId = parts[0]; kind = (NotificationKind)value; return true;
        }
        catch { return false; }
    }
}
