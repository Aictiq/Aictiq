using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// SMTP over MailKit. A connection per message, deliberately: the delivery service sends
/// a handful of messages every few seconds, and a pooled connection to a relay that has
/// gone away fails the send that discovers it rather than the one that caused it.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            throw new EmailNotConfiguredException();
        }

        var smtp = _options.Smtp!;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress!));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            // Both parts, always. HTML is what most people see; the text part is what a
            // terminal client, a screen reader in plain-text mode and a spam filter read.
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody
        }.ToMessageBody();

        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        if (smtp.AllowInvalidCertificate)
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        // Auto lets MailKit pick implicit TLS on 465 and STARTTLS elsewhere. When
        // STARTTLS is off the connection is plaintext, which is only ever right for a
        // local sink like Mailpit.
        var security = smtp.UseStartTls ? SecureSocketOptions.Auto : SecureSocketOptions.None;

        await client.ConnectAsync(smtp.Host, smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? "", cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogDebug("Sent {Subject} to {Recipient} via {Host}:{Port}",
            message.Subject, message.To, smtp.Host, smtp.Port);
    }
}
