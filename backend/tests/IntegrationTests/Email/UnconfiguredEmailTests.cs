using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Email;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// An instance with no SMTP relay. This is a supported deployment, not a broken one, and
/// these tests are what keeps it that way.
/// </summary>
[Collection("postgres")]
[Trait("Category", "Email")]
public sealed class UnconfiguredEmailTests(PostgresFixture postgres) : IAsyncLifetime
{
    private EmailTestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        // No Email section at all — the configuration a self-hoster who never set one has.
        _host = await EmailTestHost.CreateAsync(postgres, "email_unconfigured");

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void the_sender_is_the_null_one_and_says_so()
    {
        Assert.IsType<NullEmailSender>(_host.Services.GetRequiredService<IEmailSender>());
        Assert.False(_host.Services.GetRequiredService<IEmailCapabilities>().IsConfigured);
    }

    [Fact]
    public async Task email_is_healthy_and_unconfigured_never_degraded()
    {
        var ct = TestContext.Current.CancellationToken;

        var report = await _host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == EmailHealthCheck.Name, ct);

        // Degraded would take the instance out of a load balancer for deliberately not
        // sending email. It serves fine; it just cannot send.
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(EmailHealthCheck.Unconfigured, report.Entries[EmailHealthCheck.Name].Description);
    }

    [Fact]
    public async Task a_message_is_still_rendered_and_queued()
    {
        var ct = TestContext.Current.CancellationToken;

        var eventId = await _host.EnqueueAsync(
            new SendEmailRequested("ada@test.local", "invitation", new Dictionary<string, string>
            {
                ["organizationName"] = "Acme",
                ["inviterName"] = "Grace",
                ["roleName"] = "Member",
                ["acceptUrl"] = "https://aictiq.test/invitations/abc"
            }), ct);

        Assert.Equal(1, await _host.Outbox.ProcessPendingAsync(ct));

        // Queued, not dropped: the row is how an operator sees what would have been sent.
        var queued = await _host.QueryAsync(
            (db, token) => db.EmailOutbox.SingleAsync(m => m.Id == eventId, token), ct);
        Assert.Equal(EmailStatus.Pending, queued.Status);
        Assert.Contains("Acme", queued.Subject);
    }

    [Fact]
    public async Task delivery_parks_it_as_skipped_and_never_tries_again()
    {
        var ct = TestContext.Current.CancellationToken;

        await _host.EnqueueAsync(
            new SendEmailRequested("ada@test.local", "invitation", new Dictionary<string, string>
            {
                ["organizationName"] = "Acme",
                ["acceptUrl"] = "https://aictiq.test/invitations/abc"
            }), ct);
        await _host.Outbox.ProcessPendingAsync(ct);

        Assert.Equal(1, await _host.Delivery.RunOnceAsync(ct));

        var settled = await _host.QueryAsync((db, token) => db.EmailOutbox.SingleAsync(token), ct);
        Assert.Equal(EmailStatus.Skipped, settled.Status);
        Assert.Null(settled.SentAt);
        Assert.Contains("No SMTP relay", settled.LastError);

        // Terminal. Retrying cannot conjure a relay, so the row is out of the sweep's
        // reach even once every backoff has elapsed — the queue of an instance that will
        // never send stays bounded rather than growing a retry at a time.
        await _host.ExpireBackoffAsync(ct);
        Assert.Equal(0, await _host.Delivery.RunOnceAsync(ct));
    }
}
