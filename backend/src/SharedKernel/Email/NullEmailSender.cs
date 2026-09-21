using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// The sender an instance with no relay gets. It throws rather than silently succeeding:
/// a swallowed send leaves a row marked delivered and a person waiting for a message that
/// was never even attempted. The delivery service recognises this exception specifically
/// and parks the message as <c>skipped</c> instead of burning a retry budget on it.
/// </summary>
public sealed class NullEmailSender : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        throw new EmailNotConfiguredException();
}

public sealed class EmailCapabilities(IOptions<EmailOptions> options) : IEmailCapabilities
{
    public bool IsConfigured { get; } = options.Value.IsConfigured;
}
