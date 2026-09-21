using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Email;

/// <summary>
/// Reports whether this instance can send email, on <c>/health/ready</c>.
///
/// Always <b>Healthy</b>. An instance with no relay is a supported deployment — the
/// invitation screens fall back to copyable links — so "unconfigured" is a fact about the
/// deployment, not a fault to keep traffic away from. It is published as a one-word
/// description rather than a status precisely so a readiness probe stays a readiness
/// probe; the registration tags it <c>public</c>, which is what puts it in the endpoint's
/// body (see ServiceDefaults' MapDefaultEndpoints).
///
/// It does not dial the relay. A probe that opened an SMTP connection every few seconds
/// would look like a scanner to most providers, and a relay that is briefly unreachable
/// is something the delivery service's retries already handle.
/// </summary>
public sealed class EmailHealthCheck(IOptions<EmailOptions> options) : IHealthCheck
{
    public const string Name = "email";

    public const string Configured = "configured";
    public const string Unconfigured = "unconfigured";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy(
            options.Value.IsConfigured ? Configured : Unconfigured));
}
