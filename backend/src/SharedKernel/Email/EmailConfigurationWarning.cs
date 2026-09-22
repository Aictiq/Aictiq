using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Email;

public sealed class EmailConfigurationWarning(IOptions<EmailOptions> options, ILogger<EmailConfigurationWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.HasSmtpConfiguration && string.IsNullOrWhiteSpace(options.Value.BaseUrl))
            logger.LogWarning("Outbound email is configured but Email:BaseUrl is missing; link-bearing email will be skipped.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
