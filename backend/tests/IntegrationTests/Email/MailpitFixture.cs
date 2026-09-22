using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// A real SMTP server for the email tests - the same sink development runs (Aspire's
/// <c>mailpit</c> container, and the compose profile of the same name).
///
/// Mailpit rather than a fake <c>IEmailSender</c> because the thing worth proving is that
/// a message we hand to MailKit is one a server will accept and that arrives with both
/// bodies intact. A stub sender proves that our own code called our own code.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.28")
        // Accept anything: a sink that rejected credentials would be testing Mailpit's
        // auth rather than our delivery path.
        .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")
        .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")
        .WithPortBinding(1025, assignRandomHostPort: true)
        .WithPortBinding(8025, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
        .Build();

    public string Host { get; private set; } = "";
    public int SmtpPort { get; private set; }

    private HttpClient _api = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Host = _container.Hostname;
        SmtpPort = _container.GetMappedPublicPort(1025);
        _api = new HttpClient
        {
            BaseAddress = new Uri($"http://{Host}:{_container.GetMappedPublicPort(8025)}")
        };
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Waits for a message to the given recipient. SMTP delivery is asynchronous on both
    /// sides, so this polls rather than asserting on the first look.
    /// </summary>
    public async Task<MailpitMessage> WaitForMessageAsync(
        string recipient, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));

        while (DateTime.UtcNow < deadline)
        {
            var found = await FindAsync(recipient, cancellationToken);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException($"No message to {recipient} arrived at Mailpit.");
    }

    /// <summary>How many messages the sink has, for asserting that something did NOT send.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("/api/v1/messages?limit=200", cancellationToken);
        return document.RootElement.GetProperty("messages").GetArrayLength();
    }

    public async Task ClearAsync(CancellationToken cancellationToken) =>
        (await _api.DeleteAsync("/api/v1/messages", cancellationToken)).EnsureSuccessStatusCode();

    private async Task<MailpitMessage?> FindAsync(string recipient, CancellationToken cancellationToken)
    {
        using var list = await GetAsync("/api/v1/messages?limit=200", cancellationToken);
        foreach (var summary in list.RootElement.GetProperty("messages").EnumerateArray())
        {
            var matches = summary.GetProperty("To").EnumerateArray()
                .Any(to => string.Equals(
                    to.GetProperty("Address").GetString(), recipient, StringComparison.OrdinalIgnoreCase));
            if (!matches)
            {
                continue;
            }

            var id = summary.GetProperty("ID").GetString()!;
            using var message = await GetAsync($"/api/v1/message/{id}", cancellationToken);
            return new MailpitMessage(
                message.RootElement.GetProperty("Subject").GetString() ?? "",
                message.RootElement.GetProperty("HTML").GetString() ?? "",
                message.RootElement.GetProperty("Text").GetString() ?? "",
                message.RootElement.GetProperty("From").GetProperty("Address").GetString() ?? "");
        }

        return null;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        var response = await _api.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

public sealed record MailpitMessage(string Subject, string Html, string Text, string From);
