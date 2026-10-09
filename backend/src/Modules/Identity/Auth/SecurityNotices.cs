using System.Globalization;
using Microsoft.AspNetCore.Http;
using Aictiq.Modules.Identity.Domain;
using Aictiq.SharedKernel.Email;
using Aictiq.SharedKernel.Outbox;

namespace Aictiq.Modules.Identity.Auth;

/// <summary>What happened to the way someone signs in, worded for the mail that tells them.</summary>
public enum SecurityNotice
{
    TwoFactorEnabled,
    TwoFactorDisabled,
    RecoveryCodesRegenerated,
    RecoveryCodeUsed,
    PasskeyAdded,
    PasskeyRemoved,
}

/// <summary>
/// "Something changed how you sign in" - mailed to the account's own address whenever a
/// second factor or a passkey is added or removed, or a recovery code is spent.
///
/// The mail is the person's only chance to notice a change they did not make, so it goes
/// through the outbox like every other email and is staged on the caller's context: it
/// commits with the caller's next save. Nothing secret goes in it - no code, no key - only
/// what happened and when.
/// </summary>
public static class SecurityNotices
{
    public const string Template = "security-notice";

    public static void Queue(
        IdentityDbContext db, HttpContext http, EmailOptions emailOptions, ApplicationUser user,
        SecurityNotice notice, DateTimeOffset now, string? detail = null)
    {
        if (string.IsNullOrEmpty(user.Email))
        {
            return;
        }

        var (headline, summary) = Describe(notice, detail);
        var origin = string.IsNullOrWhiteSpace(emailOptions.BaseUrl)
            ? $"{http.Request.Scheme}://{http.Request.Host}"
            : emailOptions.BaseUrl.TrimEnd('/');

        db.Set<OutboxMessage>().Add(OutboxMessage.From(new SendEmailRequested(user.Email, Template,
            new Dictionary<string, string>
            {
                ["recipientName"] = user.FirstName,
                ["headline"] = headline,
                ["summary"] = summary,
                ["occurredOn"] = now.UtcDateTime.ToString("d MMMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture),
                ["securityUrl"] = $"{origin}/settings/security",
            })));
    }

    private static (string Headline, string Summary) Describe(SecurityNotice notice, string? detail) => notice switch
    {
        SecurityNotice.TwoFactorEnabled => ("Two-factor authentication turned on",
            "Two-factor authentication was turned on. Signing in with your password now also asks for a code from your authenticator app."),
        SecurityNotice.TwoFactorDisabled => ("Two-factor authentication turned off",
            "Two-factor authentication was turned off. Signing in with your password no longer asks for a code."),
        SecurityNotice.RecoveryCodesRegenerated => ("New recovery codes",
            "A new set of two-factor recovery codes was generated. The previous codes no longer work."),
        SecurityNotice.RecoveryCodeUsed => ("Recovery code used",
            $"A recovery code was used to sign in. {(detail is null ? "" : detail + " ")}Generate a new set from your security settings if you are running low."),
        SecurityNotice.PasskeyAdded => ("Passkey added",
            $"A passkey{Named(detail)} was added. It can now be used to sign in without a password."),
        SecurityNotice.PasskeyRemoved => ("Passkey removed",
            $"A passkey{Named(detail)} was removed and can no longer be used to sign in."),
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null),
    };

    private static string Named(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $" called \"{name}\"";
}
