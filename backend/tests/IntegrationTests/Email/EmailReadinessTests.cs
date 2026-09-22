using System.Net;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.SharedKernel.Email;

namespace Aictiq.IntegrationTests.Email;

/// <summary>
/// Email is optional, proved over HTTP against the real API: with SMTP unset
/// the service starts and says so on its readiness endpoint.
/// </summary>
[Collection("postgres")]
[Trait("Category", "Email")]
public sealed class EmailReadinessTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "email_readiness");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task readiness_reports_email_unconfigured_without_going_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();

        var response = await client.GetAsync("/health/ready", ct);

        // Healthy, not Degraded: an instance that deliberately does not send email is not
        // an instance to keep traffic away from.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            EmailHealthCheck.Unconfigured,
            body.RootElement.GetProperty("checks").GetProperty(EmailHealthCheck.Name).GetString());
    }

    /// <summary>
    /// The compose bundle passes <c>Email__FromAddress: ${EMAIL_FROM_ADDRESS:-}</c>, so an
    /// instance that sends no email binds the <em>empty string</em> rather than nothing at
    /// all - which DataAnnotations' [EmailAddress] refuses, dragging readiness to Unhealthy
    /// on a deployment that is supposed to be supported. EmailOptionsValidator only checks
    /// an address that was actually set.
    /// </summary>
    [Fact]
    public async Task an_empty_from_address_is_no_email_rather_than_a_broken_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(postgres, garage, "email_blank_from",
            settings => settings["Email:FromAddress"] = "");
        using var client = context.Anonymous();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            EmailHealthCheck.Unconfigured,
            body.RootElement.GetProperty("checks").GetProperty(EmailHealthCheck.Name).GetString());
    }

    [Fact]
    public async Task readiness_publishes_nothing_but_the_checks_that_opted_in()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();

        using var body = JsonDocument.Parse(
            await (await client.GetAsync("/health/ready", ct)).Content.ReadAsStringAsync(ct));

        // /health is reachable from the internet in the compose deployment, and the
        // storage check's description names the object store's internal endpoint.
        var published = body.RootElement.GetProperty("checks").EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal([EmailHealthCheck.Name], published);
    }
}
