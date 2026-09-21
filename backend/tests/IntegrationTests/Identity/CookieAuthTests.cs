using System.Net;
using System.Net.Http.Json;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Identity;

/// <summary>
/// The browser half of authentication: the API sets httpOnly cookies, the SPA never sees
/// a token, and cookie-authenticated writes must carry the CSRF header. The bearer path
/// (CLI, MCP) has to keep working unchanged alongside it.
/// </summary>
[Trait("Category", "Auth")]
[Collection("postgres")]
public sealed class CookieAuthTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "cookie_auth");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task cookie_login_returns_no_token_in_the_body_and_sets_httponly_cookies()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-login@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        var response = await browser.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, ApiTestContext.DefaultPassword), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The whole point: nothing the browser's JavaScript could read.
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(ct);
        Assert.Null(body!.AccessToken);
        Assert.Null(body.RefreshToken);
        Assert.Equal(email, body.User.Email);

        var cookies = SetCookies(response);
        var access = Assert.Single(cookies, c => c.StartsWith($"{AuthCookies.AccessCookieName}="));
        var refresh = Assert.Single(cookies, c => c.StartsWith($"{AuthCookies.RefreshCookieName}="));

        Assert.Contains("httponly", access, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", access, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", access, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("httponly", refresh, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refresh, StringComparison.OrdinalIgnoreCase);
        // Scoped to the refresh endpoint, so no other request can leak it.
        Assert.Contains($"path={AuthCookies.RefreshCookiePath}", refresh, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task the_access_cookie_authenticates_requests_that_carry_no_authorization_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-session@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        await CookieLoginAsync(browser, email, ct);

        var session = await browser.GetAsync("/api/v1/auth/session", ct);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        var user = await session.Content.ReadFromJsonAsync<SessionResponse>(ct);
        Assert.Equal(email, user!.Email);
        Assert.False(user.IsAgent);
        Assert.Contains(SharedKernel.Roles.User, user.Roles);
    }

    [Fact]
    public async Task session_is_401_without_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.GetAsync("/api/v1/auth/session", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task refresh_rotates_both_cookies_and_the_old_refresh_cookie_kills_the_family()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-refresh@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        var login = await CookieLoginAsync(browser, email, ct);
        var firstRefresh = CookieValue(login, AuthCookies.RefreshCookieName);

        var refreshed = await browser.PostAsync("/api/v1/auth/refresh", content: null, ct);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var secondRefresh = CookieValue(refreshed, AuthCookies.RefreshCookieName);
        Assert.NotEqual(firstRefresh, secondRefresh);
        Assert.NotNull(CookieValue(refreshed, AuthCookies.AccessCookieName));

        // The rotated cookie still works...
        using var afterRotation = _context.Browser();
        Send(afterRotation, refreshed);
        Assert.Equal(HttpStatusCode.OK,
            (await afterRotation.GetAsync("/api/v1/auth/session", ct)).StatusCode);

        // ...and replaying the spent one revokes the whole family (reuse detection).
        using var replay = _context.Browser();
        replay.DefaultRequestHeaders.Add("Cookie", $"{AuthCookies.RefreshCookieName}={firstRefresh}");
        var reuse = await replay.PostAsync("/api/v1/auth/refresh", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        // The dead cookies are cleared so the SPA stops retrying a token that cannot work.
        Assert.Contains(SetCookies(reuse), c => c.StartsWith($"{AuthCookies.RefreshCookieName}="));

        using var afterReuse = _context.Browser();
        Send(afterReuse, refreshed);
        var revoked = await afterReuse.PostAsync("/api/v1/auth/refresh", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task refresh_without_a_cookie_is_401_not_a_validation_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = _context.Browser();
        var response = await browser.PostAsync("/api/v1/auth/refresh", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task logout_clears_both_cookies_and_revokes_the_family()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-logout@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        var login = await CookieLoginAsync(browser, email, ct);

        var logout = await browser.PostAsync("/api/v1/auth/logout", content: null, ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Both cookies are expired in the past so the browser drops them.
        var cleared = SetCookies(logout);
        Assert.Contains(cleared, c => c.StartsWith($"{AuthCookies.AccessCookieName}=;"));
        Assert.Contains(cleared, c => c.StartsWith($"{AuthCookies.RefreshCookieName}=;"));

        // And the refresh token is dead server-side, not merely forgotten by the client.
        using var replay = _context.Browser();
        Send(replay, login);
        var refresh = await replay.PostAsync("/api/v1/auth/refresh", content: null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task a_cookie_authenticated_write_without_the_csrf_header_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-csrf@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        var login = await CookieLoginAsync(browser, email, ct);

        // Same cookies, but a client that does not send X-Aictiq-Request — exactly what a
        // cross-site form post looks like to the API.
        using var forged = _context.Anonymous();
        Send(forged, login);

        var write = await forged.PostAsync("/api/v1/auth/logout", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        // Reads are untouched: they change nothing.
        var read = await forged.GetAsync("/api/v1/auth/session", ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task a_cookie_authenticated_write_with_the_csrf_header_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "cookie-csrf-ok@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        await CookieLoginAsync(browser, email, ct);

        var write = await browser.PostAsync("/api/v1/auth/logout", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
    }

    [Fact]
    public async Task bearer_writes_never_need_the_csrf_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("bearer-csrf@test.local");

        using var client = _context.ClientFor(auth);
        var write = await client.PostAsync("/api/v1/auth/logout", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, write.StatusCode);
    }

    /// <summary>
    /// An explicit <c>?mode=body</c> beats the header sniff, so a browser-based tool can
    /// still take the tokens itself.
    /// </summary>
    [Fact]
    public async Task explicit_body_mode_wins_over_the_request_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = "mode-body@test.local";
        await _context.RegisterAsync(email);

        using var browser = _context.Browser();
        var response = await browser.PostAsJsonAsync("/api/v1/auth/login?mode=body",
            new LoginRequest(email, ApiTestContext.DefaultPassword), ct);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(ct);
        Assert.NotNull(body!.AccessToken);
        Assert.Empty(SetCookies(response));
    }

    private async Task<HttpResponseMessage> CookieLoginAsync(
        HttpClient browser, string email, CancellationToken ct)
    {
        var response = await browser.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, ApiTestContext.DefaultPassword), ct);
        response.EnsureSuccessStatusCode();
        Send(browser, response);
        return response;
    }

    /// <summary>
    /// HttpClient does not keep a cookie jar here, so tests replay the cookies the API
    /// set — which is also what makes it easy to hand the same cookies to a client that
    /// omits the CSRF header.
    /// </summary>
    private static void Send(HttpClient client, HttpResponseMessage from)
    {
        var jar = string.Join("; ", SetCookies(from)
            .Select(c => c.Split(';', 2)[0])
            .Where(c => !c.EndsWith('=')));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", jar);
    }

    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    private static string? CookieValue(HttpResponseMessage response, string name) =>
        SetCookies(response)
            .FirstOrDefault(c => c.StartsWith($"{name}="))
            ?.Split(';', 2)[0][(name.Length + 1)..];
}
