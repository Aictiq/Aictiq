using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel.Email;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// The whole path, end to end: a module raises an event, the outbox delivers it, the
/// handler queues a rendered message, the delivery sweep hands it to a real SMTP server.
/// </summary>
[Collection("postgres")]
[Trait("Category", "Email")]
public sealed class EmailDeliveryTests(PostgresFixture postgres, MailpitFixture mailpit)
    : IClassFixture<MailpitFixture>, IAsyncLifetime
{
    private EmailTestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await EmailTestHost.CreateAsync(postgres, "email_delivery", new Dictionary<string, string?>
        {
            ["Email:Smtp:Host"] = mailpit.Host,
            ["Email:Smtp:Port"] = mailpit.SmtpPort.ToString(),
            // Mailpit speaks plaintext; STARTTLS is for a real relay.
            ["Email:Smtp:UseStartTls"] = "false",
            ["Email:FromAddress"] = "aictiq@test.local",
            ["Email:FromName"] = "Aictiq Test",
            // An instance sends link-bearing mail only once it knows the
            // public origin those links point at; without it every message is skipped.
            ["Email:BaseUrl"] = "https://aictiq.test"
        });

        await mailpit.ClearAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static SendEmailRequested Invitation(string to) =>
        new(to, "invitation", new Dictionary<string, string>
        {
            ["organizationName"] = "Acme",
            ["inviterName"] = "Grace Hopper",
            ["roleName"] = "Member",
            ["acceptUrl"] = "https://aictiq.test/invitations/abc"
        });

    [Fact]
    public async Task an_event_becomes_a_queued_message_and_then_an_email()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipient = "ada@test.local";

        var eventId = await _host.EnqueueAsync(Invitation(recipient), ct);

        Assert.Equal(1, await _host.Outbox.ProcessPendingAsync(ct));

        // The handler renders and queues; it never touches a relay, so a slow SMTP server
        // cannot stall the outbox sweep behind it.
        var queued = await _host.QueryAsync(
            (db, token) => db.EmailOutbox.SingleAsync(m => m.Id == eventId, token), ct);
        Assert.Equal(EmailStatus.Pending, queued.Status);
        Assert.Equal(recipient, queued.ToAddress);
        Assert.Contains("Grace Hopper", queued.Subject);
        Assert.Contains("Acme", queued.Subject);

        Assert.Equal(1, await _host.Delivery.RunOnceAsync(ct));

        var message = await mailpit.WaitForMessageAsync(recipient, ct);
        Assert.Contains("Grace Hopper", message.Subject);
        Assert.Equal("aictiq@test.local", message.From);
        // Both parts, always: HTML is what most people see, text is what a terminal
        // client, a screen reader and a spam filter read.
        Assert.Contains("https://aictiq.test/invitations/abc", message.Html);
        Assert.Contains("https://aictiq.test/invitations/abc", message.Text);

        var sent = await _host.QueryAsync(
            (db, token) => db.EmailOutbox.SingleAsync(m => m.Id == eventId, token), ct);
        Assert.Equal(EmailStatus.Sent, sent.Status);
        Assert.NotNull(sent.SentAt);
        Assert.Null(sent.LastError);
    }

    [Fact]
    public async Task the_queued_row_is_keyed_by_the_event_so_a_replay_sends_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var recipient = "replay@test.local";

        await _host.EnqueueAsync(Invitation(recipient), ct);
        await _host.Outbox.ProcessPendingAsync(ct);

        // Delivery is at least once: a crash between the handler's commit and the
        // processed_at mark replays the message. That must not queue a second email.
        await _host.ReplayOutboxAsync(ct);
        await _host.Outbox.ProcessPendingAsync(ct);

        var queued = await _host.QueryAsync(
            (db, token) => db.EmailOutbox.CountAsync(m => m.ToAddress == recipient, token), ct);
        Assert.Equal(1, queued);

        await _host.Delivery.RunOnceAsync(ct);
        await mailpit.WaitForMessageAsync(recipient, ct);
    }

    [Fact]
    public async Task a_sent_message_is_not_claimed_again()
    {
        var ct = TestContext.Current.CancellationToken;

        await _host.EnqueueAsync(Invitation("once@test.local"), ct);
        await _host.Outbox.ProcessPendingAsync(ct);

        Assert.Equal(1, await _host.Delivery.RunOnceAsync(ct));
        await mailpit.WaitForMessageAsync("once@test.local", ct);

        // Even with every backoff elapsed, a settled row is out of the sweep's reach.
        await _host.ExpireBackoffAsync(ct);
        Assert.Equal(0, await _host.Delivery.RunOnceAsync(ct));
    }

    [Fact]
    public async Task a_message_waits_for_its_backoff_before_the_next_attempt()
    {
        var ct = TestContext.Current.CancellationToken;

        await _host.EnqueueAsync(Invitation("backoff@test.local"), ct);
        await _host.Outbox.ProcessPendingAsync(ct);

        var queued = await _host.QueryAsync(
            (db, token) => db.EmailOutbox.SingleAsync(token), ct);
        Assert.Equal(0, queued.Attempts);

        await _host.Delivery.RunOnceAsync(ct);

        // Claiming moves send_after forward in the same statement that increments the
        // attempt count, so a crashed sweep leaves a row that simply becomes claimable
        // again rather than one stuck in a state only a human can clear.
        var after = await _host.QueryAsync((db, token) => db.EmailOutbox.SingleAsync(token), ct);
        Assert.Equal(1, after.Attempts);
        Assert.True(after.SendAfter > queued.SendAfter);
    }

    [Fact]
    public async Task a_template_that_does_not_exist_never_becomes_a_queued_message()
    {
        var ct = TestContext.Current.CancellationToken;

        await _host.EnqueueAsync(
            new SendEmailRequested("nobody@test.local", "not-a-template", new Dictionary<string, string>()), ct);

        // The handler throws, so the outbox message is retried and eventually
        // dead-lettered - loud, and never a half-rendered email.
        await _host.Outbox.ProcessPendingAsync(ct);

        Assert.Equal(0, await _host.QueryAsync(
            (db, token) => db.EmailOutbox.CountAsync(token), ct));
    }
}
