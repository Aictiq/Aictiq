using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.Api.Infrastructure;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Turnstile;

namespace Aictiq.IntegrationTests.Identity;

/// <summary>
/// Turnstile is a server-side control or it is nothing: a script that never loads the
/// widget just posts the form. So what is proved here is the API's half - that the
/// anonymous credential endpoints refuse a request without a token Cloudflare vouched
/// for, for the right form, and fail closed when Cloudflare cannot be asked.
///
/// siteverify is replaced by a fake handler; everything between the endpoint and the
/// HTTP call is the production code.
/// </summary>
[Trait("Category", "Auth")]
public sealed class TurnstileTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string SiteKey = "1x00000000000000000000AA";
    private const string SecretKey = "1x0000000000000000000000000000000AA";

    private readonly FakeSiteVerify _cloudflare = new();
    private ApiTestContext _context = null!;
    private HttpClient _anonymous = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "turnstile",
            settings =>
            {
                settings["Turnstile:SiteKey"] = SiteKey;
                settings["Turnstile:SecretKey"] = SecretKey;
                settings["Turnstile:AllowedHostnames:0"] = "aictiq.test";
            },
            services => services.AddHttpClient(TurnstileVerifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _cloudflare),
            // Signing the administrator in at boot would itself need a solved challenge.
            signInAdmin: false);
        _anonymous = _context.Anonymous();

        // Only a token the test names is good, and only for the action in its name.
        _cloudflare.Respond = (token, _) => token.StartsWith("good-", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Success(token["good-".Length..]))
            : (HttpStatusCode.OK, """{"success":false,"error-codes":["invalid-input-response"]}""");
    }

    public async ValueTask DisposeAsync()
    {
        _anonymous.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task the_sign_in_pages_are_given_the_public_key_and_never_the_secret()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _anonymous.GetAsync("/api/v1/auth/challenge", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SiteKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_sign_in_without_a_token_is_refused_before_cloudflare_or_the_password_is_consulted()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync("/api/v1/auth/login",
            new LoginRequest(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword), token: null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemTypes.ChallengeFailed,
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Type);
        Assert.Empty(_cloudflare.Requests);
    }

    [Fact]
    public async Task a_token_cloudflare_vouches_for_signs_in_and_the_secret_goes_only_to_cloudflare()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync("/api/v1/auth/login",
            new LoginRequest(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword), "good-login", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_cloudflare.Requests);
        Assert.Contains($"secret={SecretKey}", sent, StringComparison.Ordinal);
        Assert.Contains("response=good-login", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_token_cloudflare_refuses_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync("/api/v1/auth/login",
            new LoginRequest(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword), "forged", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_token_solved_on_one_form_does_not_open_another()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync("/api/v1/auth/register",
            new RegisterRequest("crossform@test.local", ApiTestContext.DefaultPassword, "Cross", "Form"),
            "good-login", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_token_solved_on_another_host_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        _cloudflare.Respond = (_, _) => (HttpStatusCode.OK, Success("login", hostname: "elsewhere.example"));

        var response = await PostAsync("/api/v1/auth/login",
            new LoginRequest(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword), "good-login", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/register", "register")]
    [InlineData("/api/v1/auth/forgot", "forgot")]
    [InlineData("/api/v1/auth/verify-email/resend", "resend-confirmation")]
    public async Task every_anonymous_form_that_mints_something_is_challenged(string path, string action)
    {
        var ct = TestContext.Current.CancellationToken;
        object body = action == "register"
            ? new RegisterRequest($"{action}@test.local", ApiTestContext.DefaultPassword, "Form", "Filler")
            : new ForgotPasswordRequest($"{action}@test.local");

        var refused = await PostAsync(path, body, token: null, ct);
        var passed = await PostAsync(path, body, $"good-{action}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True(passed.IsSuccessStatusCode, $"{path} answered {(int)passed.StatusCode}");
    }

    [Fact]
    public async Task an_unreachable_cloudflare_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        _cloudflare.Respond = (_, _) => (HttpStatusCode.BadGateway, "");

        var response = await PostAsync("/api/v1/auth/login",
            new LoginRequest(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword), "good-login", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void the_content_security_policy_admits_cloudflare_only_where_turnstile_needs_it()
    {
        var policy = SecurityHeadersMiddleware.WithTurnstile(SecurityHeadersMiddleware.ProductionCsp);

        Assert.Contains($"script-src 'self' {SecurityHeadersMiddleware.TurnstileOrigin}", policy);
        Assert.Contains($"frame-src 'self' {SecurityHeadersMiddleware.TurnstileOrigin}", policy);
        Assert.Contains("connect-src 'self';", policy);
        Assert.Contains("upgrade-insecure-requests", policy);
        Assert.DoesNotContain(SecurityHeadersMiddleware.TurnstileOrigin, SecurityHeadersMiddleware.ProductionCsp);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, body.GetType()),
        };
        if (token is not null)
        {
            request.Headers.Add(TurnstileEndpointFilter.HeaderName, token);
        }

        return await _anonymous.SendAsync(request, ct);
    }

    private static string Success(string action, string hostname = "aictiq.test") =>
        $$"""{"success":true,"error-codes":[],"hostname":"{{hostname}}","action":"{{action}}","challenge_ts":"2026-09-24T10:00:00Z"}""";

    /// <summary>Stands in for challenges.cloudflare.com: records each form it is sent.</summary>
    private sealed class FakeSiteVerify : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        public Func<string, string, (HttpStatusCode Status, string Body)> Respond { get; set; } =
            (_, _) => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(form);
            var fields = form.Split('&')
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            var (status, body) = Respond(fields.GetValueOrDefault("response", ""), fields.GetValueOrDefault("secret", ""));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
