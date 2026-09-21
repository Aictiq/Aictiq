using System.ComponentModel.DataAnnotations;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// Configuration for outbound email, bound from the <c>Email</c> section.
///
/// Email is <em>optional</em> in Aictiq. A self-hosted instance with no SMTP relay is a
/// supported deployment, not a broken one: invitations fall back to a link the inviter
/// copies out by hand. Nothing here is <c>[Required]</c> for that reason — the whole
/// section being absent is the "no email" configuration, and it must not stop the
/// service from starting.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public SmtpOptions? Smtp { get; set; }

    /// <summary>
    /// The envelope sender. Without one there is nothing to send from. The attribute is
    /// documentation and is applied by <see cref="EmailOptionsValidator"/>, not by
    /// ValidateDataAnnotations: compose binds an unset variable to the <em>empty string</em>,
    /// which <see cref="EmailAddressAttribute"/> rejects — and "no email" is a supported
    /// deployment, not a start-up failure.
    /// </summary>
    [EmailAddress]
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Aictiq";

    /// <summary>
    /// Absolute base URL the links in emails point at — the address a recipient's browser
    /// can actually reach, which is not necessarily the one the API binds to.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Whether this instance can send at all. Read through <see cref="IEmailCapabilities"/>
    /// rather than here, so callers depend on the question and not on the options object.
    /// </summary>
    public bool HasSmtpConfiguration => Smtp is { Host.Length: > 0 } && !string.IsNullOrWhiteSpace(FromAddress);
    /// <summary>Link-bearing mail is safe only when its public origin is configured.</summary>
    public bool IsConfigured => HasSmtpConfiguration && !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// STARTTLS when the server offers it, which is what a submission relay on 587 does.
    /// Turned off for a local sink (Mailpit) that speaks plain SMTP on 1025.
    /// </summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>
    /// Accept a certificate that does not validate. Only for a relay on a private network
    /// with a self-signed certificate — it removes the guarantee that the relay is the
    /// one you meant, so it is off unless someone says otherwise in writing.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}
