namespace Aictiq.SharedKernel.Email;

/// <summary>One rendered message, ready to hand to a relay.</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// The transport. Callers never reach for this directly on a request path - email is
/// enqueued through the outbox and sent by Workers, because a relay that is slow or down
/// must not be able to fail the write that occasioned the message.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Whether this instance can send email at all, so a screen can say "email is not
/// configured - share the invitation link instead" rather than promising a message that
/// will never arrive.
/// </summary>
public interface IEmailCapabilities
{
    bool IsConfigured { get; }
}

/// <summary>
/// Thrown by <see cref="NullEmailSender"/>. Terminal, never retried: no number of
/// attempts will conjure a relay that was never configured.
/// </summary>
public sealed class EmailNotConfiguredException()
    : InvalidOperationException("Outbound email requires Email:Smtp:Host, Email:FromAddress, and Email:BaseUrl.");
