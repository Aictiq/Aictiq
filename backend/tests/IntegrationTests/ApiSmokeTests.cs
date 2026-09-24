using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Api.Infrastructure;

namespace Aictiq.IntegrationTests;

public sealed class ApiSmokeTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "smoke");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task health_endpoints_answer_without_authentication(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task meta_is_public_and_exposes_only_the_opt_in_update_configuration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();

        var response = await client.GetAsync("/api/v1/meta", ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        Assert.Matches("^\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?$", root.GetProperty("version").GetString() ?? string.Empty);
        var updates = root.GetProperty("updateCheck");
        Assert.False(updates.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, updates.GetProperty("repository").ValueKind);
    }

    [Fact]
    public async Task every_response_carries_the_security_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync("/health/live", ct);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("Permissions-Policy"));
    }

    /// <summary>
    /// The API now serves the SPA, so it owns the CSP - but only for the HTML it
    /// returns. A CSP on a JSON body protects nothing and confuses non-browser clients,
    /// and API responses must never be cached.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/does-not-exist")]
    [InlineData("/health/live")]
    public async Task api_routes_are_no_store_and_carry_no_csp(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync(path, ct);

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// A plain-HTTP instance is a supported deployment (AICTIQ_URL=http://...), and
    /// <c>upgrade-insecure-requests</c> would rewrite every same-origin asset request to
    /// https on a host with no listener there - a blank page. Browsers exempt localhost
    /// from the upgrade, so only a real hostname or address ever showed it.
    /// </summary>
    [Fact]
    public void the_plain_http_policy_is_the_https_one_without_the_upgrade_directive()
    {
        Assert.Contains("upgrade-insecure-requests", SecurityHeadersMiddleware.ProductionCsp);
        Assert.DoesNotContain("upgrade-insecure-requests", SecurityHeadersMiddleware.ProductionCspWithoutUpgrade);
        Assert.Equal(
            SecurityHeadersMiddleware.ProductionCsp,
            SecurityHeadersMiddleware.ProductionCspWithoutUpgrade + "; upgrade-insecure-requests");
    }

    /// <summary>
    /// The SPA signs out and rotates its cookie session with a POST that has no body -
    /// in cookie mode both tokens are httpOnly, so there is nothing for it to send. An
    /// inferred JSON body parameter used to make these two endpoints match
    /// <c>application/json</c> only, so a bodyless POST matched nothing and fell through
    /// to the API's 404 fallback: signing out left the cookies in place and silent
    /// refresh could never succeed. Both must answer on their own terms instead.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/auth/logout", HttpStatusCode.NoContent)]
    [InlineData("/api/v1/auth/refresh", HttpStatusCode.Unauthorized)]
    public async Task a_bodyless_post_reaches_the_endpoint_and_is_not_a_404(
        string path, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        // The CSRF header is what puts the API in cookie mode, which is the SPA's path.
        request.Headers.Add("X-Aictiq-Request", "1");

        var response = await client.SendAsync(request, ct);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task validation_errors_are_rfc9457_problem_json_with_field_errors_and_trace_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(null, null, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("email", problem!.Errors.Keys);
        Assert.Contains("password", problem.Errors.Keys);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task unhandled_routes_return_problem_json_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync("/api/v1/does-not-exist", ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task openapi_document_is_served()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync("/openapi/v1.json", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        Assert.Equal("Aictiq REST API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("cookieAuth", out _));
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("personalAccessToken", out _));
        Assert.True(root.GetProperty("paths").GetProperty("/api/v1/auth/login").GetProperty("post").TryGetProperty("operationId", out _));
    }

    [Fact]
    public async Task scalar_reference_is_served_at_docs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync("/docs", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
