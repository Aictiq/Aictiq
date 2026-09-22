using System.Net;
using System.Net.Http.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Identity.External;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// Signing in with Google and GitHub, driven through the framework's real OAuth handler
/// against a <see cref="FakeOAuthProvider"/> back channel - so the authorize redirect, the
/// state parameter, PKCE and the correlation cookie are all under test, not stubbed.
///
/// The rule these exist to protect: <b>an unverified provider email proves nothing</b>. It
/// must never find an account and never create one, because a provider that lets someone
/// type an address they do not own would otherwise be a way into somebody else's Aictiq.
/// </summary>
[Trait("Category", "Auth")]
public sealed class ExternalSignInTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private readonly FakeOAuthProvider _provider = new();
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "external_auth",
            configure: settings =>
            {
                settings["Auth:Google:ClientId"] = "google-client";
                settings["Auth:Google:ClientSecret"] = "google-secret";
                settings["Auth:GitHub:ClientId"] = "github-client";
                settings["Auth:GitHub:ClientSecret"] = "github-secret";
            },
            configureServices: services => _provider.Install(services));

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    // ------------------------------------------------------------------------- discovery

    [Fact]
    public async Task the_login_page_is_told_which_providers_this_instance_has()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = _context.Anonymous();

        var providers = await anonymous.GetFromJsonAsync<List<ExternalProviderView>>(
            "/api/v1/auth/providers", ApiTestContext.Json, ct);

        Assert.Equal(["google", "github"], providers!.Select(p => p.Name));
        Assert.Equal("GitHub", providers!.Single(p => p.Name == "github").DisplayName);
    }

    [Fact]
    public async Task a_provider_this_instance_has_no_credentials_for_is_not_offered()
    {
        var ct = TestContext.Current.CancellationToken;
        // A deployment with no OAuth credentials at all is supported: the login page draws
        // no buttons and the password form is the whole of it.
        await using var plain = await ApiTestContext.CreateAsync(postgres, garage, "external_none");

        // Not the default client: it follows redirects, and the redirect is the assertion.
        using var anonymous = plain.Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var providers = await anonymous.GetFromJsonAsync<List<ExternalProviderView>>(
            "/api/v1/auth/providers", ApiTestContext.Json, ct);
        var start = await anonymous.GetAsync("/api/v1/auth/external/google", ct);

        Assert.Empty(providers!);
        Assert.Equal("/login?error=provider-unavailable", start.Headers.Location?.ToString());
    }

    // ------------------------------------------------------------------------- signing in

    [Fact]
    public async Task a_new_person_gets_an_account_a_linked_login_and_a_session()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();

        var landing = await SignInAsync(browser, ExternalProviders.Google, next: "/items", ct: ct);

        Assert.Equal("/items", landing);
        // The provider verified the address, so there is nothing left for us to confirm -
        // and no password, because an account with one nobody knows is worse than none.
        var (confirmed, hasPassword) = await ReadAccountAsync("ada@test.local", ct);
        Assert.True(confirmed);
        Assert.False(hasPassword);
        Assert.Equal(1, await CountLoginsAsync("ada@test.local", ct));

        // And the browser is signed in: the callback set the same httpOnly cookies the
        // password flow does, because a redirect has nowhere to put a bearer token.
        var session = await browser.GetFromJsonAsync<SessionResponse>(
            "/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal("ada@test.local", session!.Email);
        Assert.Equal("Ada Lovelace", session.FullName);
    }

    [Fact]
    public async Task signing_in_again_finds_the_link_rather_than_making_a_second_account()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var first = Browser())
        {
            await SignInAsync(first, ExternalProviders.Google, ct: ct);
        }

        // Same provider key, but the address has since changed at the provider. The link is
        // the whole answer - no email lookup happens at all.
        _provider.Email = "ada.lovelace@test.local";
        using var second = Browser();
        var landing = await SignInAsync(second, ExternalProviders.Google, ct: ct);

        Assert.Equal("/", landing);
        // The account keeps the address it was created with; nothing looked the new one up.
        Assert.Equal(1, await CountAccountsAsync("ada@test.local", ct));
        Assert.Equal(0, await CountAccountsAsync("ada.lovelace@test.local", ct));
    }

    [Fact]
    public async Task a_verified_address_links_to_the_account_that_already_confirmed_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");
        await ConfirmEmailAsync("ada@test.local", ct);

        using var browser = Browser();
        var landing = await SignInAsync(browser, ExternalProviders.Google, ct: ct);

        Assert.Equal("/", landing);
        Assert.Equal(1, await CountAccountsAsync("ada@test.local", ct));
        Assert.Equal(1, await CountLoginsAsync("ada@test.local", ct));
    }

    [Fact]
    public async Task an_account_that_never_proved_its_address_is_not_handed_over()
    {
        var ct = TestContext.Current.CancellationToken;
        // Registered with a password but never confirmed. Linking now would give the
        // account to whoever proved the address *second*.
        await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");

        using var browser = Browser();
        var landing = await SignInAsync(browser, ExternalProviders.Google, ct: ct);

        Assert.Equal($"/login?error={ExternalAuthEndpoints.LinkRequiredError}", landing);
        Assert.Equal(0, await CountLoginsAsync("ada@test.local", ct));
    }

    [Fact]
    public async Task an_unverified_provider_address_creates_nothing_and_finds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        _provider.EmailVerified = false;

        using var browser = Browser();
        var landing = await SignInAsync(browser, ExternalProviders.Google, ct: ct);

        Assert.Equal($"/login?error={ExternalAuthEndpoints.EmailUnverifiedError}", landing);
        Assert.Equal(0, await CountAccountsAsync("ada@test.local", ct));
    }

    [Fact]
    public async Task github_is_asked_for_the_primary_verified_address_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();

        var landing = await SignInAsync(browser, ExternalProviders.GitHub, ct: ct);

        Assert.Equal("/", landing);
        // The fake's profile carries a *different*, public-but-unverified address. Using it
        // would be the bug this asserts against.
        Assert.Equal(1, await CountLoginsAsync("ada@test.local", ct));
        Assert.Equal(0, await CountLoginsAsync("public-but-unverified@test.local", ct));
    }

    [Fact]
    public async Task github_without_the_email_scope_cannot_sign_anyone_in()
    {
        var ct = TestContext.Current.CancellationToken;
        // The scope can be declined at the consent screen. Then no address is known to be
        // theirs, and falling back to the public profile email is exactly what must not
        // happen.
        _provider.EmailScopeDenied = true;

        using var browser = Browser();
        var landing = await SignInAsync(browser, ExternalProviders.GitHub, ct: ct);

        Assert.Equal($"/login?error={ExternalAuthEndpoints.EmailUnverifiedError}", landing);
        Assert.Equal(0, await CountAccountsAsync("ada@test.local", ct));
    }

    // ---------------------------------------------------------------------- where it lands

    [Fact]
    public async Task an_invitation_carried_through_the_flow_lands_on_its_own_page()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();

        var landing = await SignInAsync(browser, ExternalProviders.Google, invite: "tok-123", ct: ct);

        // Not accepted behind their back: they arrive at the page that says what they are
        // joining, now signed in.
        Assert.Equal("/invite/tok-123", landing);
    }

    [Theory]
    [InlineData("https://evil.test/steal")]
    [InlineData("//evil.test/steal")]
    [InlineData("/\\evil.test")]
    public async Task next_can_only_ever_be_a_path_on_this_origin(string next)
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();

        var landing = await SignInAsync(browser, ExternalProviders.Google, next: next, ct: ct);

        // An absolute URL here would turn sign-in into an open redirect - the classic way
        // to make a phishing link look like it came from the product.
        Assert.Equal("/", landing);
    }

    // -------------------------------------------------------------------------- the state

    [Fact]
    public async Task a_callback_with_no_state_at_all_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();

        var response = await browser.GetAsync(
            "/api/v1/auth/external/google/oauth?code=whatever", ct);

        // The handler will not exchange a code it cannot tie to a challenge it issued -
        // and the person sees the login page rather than a 500.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/login?error={ExternalAuthEndpoints.ProviderFailedError}",
            response.Headers.Location?.ToString());
        Assert.Equal(0, await CountAccountsAsync("ada@test.local", ct));
    }

    [Fact]
    public async Task a_state_without_its_correlation_cookie_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var challenger = Browser();
        var state = await ChallengeAsync(challenger, ExternalProviders.Google, null, null, ct);

        // A different browser: it holds the state (which travels in the URL, so an attacker
        // can have it) but not the cookie the handler set alongside it.
        using var stranger = Browser();
        var response = await stranger.GetAsync(
            $"/api/v1/auth/external/google/oauth?code=x&state={Uri.EscapeDataString(state)}", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/login?error={ExternalAuthEndpoints.ProviderFailedError}",
            response.Headers.Location?.ToString());
        Assert.Equal(0, await CountAccountsAsync("ada@test.local", ct));
    }

    // --------------------------------------------------------------------------- linking

    [Fact]
    public async Task someone_signed_in_can_link_a_provider_and_then_unlink_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");

        using var browser = Browser();
        await SignInWithPasswordAsync(browser, "ada@test.local", ct);

        var landing = await CompleteAsync(browser, ExternalProviders.Google,
            await ChallengeAsync(browser, ExternalProviders.Google, "/settings", null, ct, link: true), ct);
        Assert.Equal("/settings", landing);

        var logins = await browser.GetFromJsonAsync<List<ExternalLoginView>>(
            "/api/v1/me/logins", ApiTestContext.Json, ct);
        var login = Assert.Single(logins!);
        Assert.Equal("google", login.Provider);
        // They still have a password, so removing this is not locking themselves out.
        Assert.True(login.CanUnlink);

        var removed = await browser.DeleteAsync("/api/v1/me/logins/google", ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await CountLoginsAsync("ada@test.local", ct));
        Assert.NotNull(auth.User.Id);
    }

    [Fact]
    public async Task the_only_way_into_an_account_cannot_be_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var browser = Browser();
        await SignInAsync(browser, ExternalProviders.Google, ct: ct);

        var logins = await browser.GetFromJsonAsync<List<ExternalLoginView>>(
            "/api/v1/me/logins", ApiTestContext.Json, ct);
        var response = await browser.DeleteAsync("/api/v1/me/logins/google", ct);

        // This account has no password - the provider is the whole of its front door.
        Assert.False(Assert.Single(logins!).CanUnlink);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await CountLoginsAsync("ada@test.local", ct));
    }

    [Fact]
    public async Task linking_requires_a_session_of_your_own()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = Browser();

        var response = await anonymous.GetAsync("/api/v1/auth/external/google/link", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    /// <summary>
    /// A client that behaves like a browser for this flow: it keeps cookies and does not
    /// follow redirects, so each hop can be inspected.
    /// </summary>
    private HttpClient Browser()
    {
        var client = _context.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(
            Aictiq.Modules.Identity.Auth.AuthCookies.RequestHeaderName, "1");
        return client;
    }

    /// <summary>Challenge, then callback. Returns wherever the browser is finally sent.</summary>
    private async Task<string> SignInAsync(
        HttpClient browser, string provider, string? next = null, string? invite = null,
        CancellationToken ct = default)
    {
        var state = await ChallengeAsync(browser, provider, next, invite, ct);
        return await CompleteAsync(browser, provider, state, ct);
    }

    /// <summary>
    /// Starts the flow and reads <c>state</c> back out of the authorize URL - which is
    /// exactly what a real provider does before redirecting back.
    /// </summary>
    private static async Task<string> ChallengeAsync(
        HttpClient browser, string provider, string? next, string? invite,
        CancellationToken ct, bool link = false)
    {
        var query = new List<string>();
        if (next is not null) query.Add($"next={Uri.EscapeDataString(next)}");
        if (invite is not null) query.Add($"invite={Uri.EscapeDataString(invite)}");
        var suffix = query.Count == 0 ? "" : $"?{string.Join('&', query)}";

        var path = link ? $"/api/v1/auth/external/{provider}/link" : $"/api/v1/auth/external/{provider}";
        var response = await browser.GetAsync($"{path}{suffix}", ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorize = response.Headers.Location!;
        Assert.Contains(provider == "google" ? "accounts.google.com" : "github.com", authorize.Host);

        return HttpUtility.ParseQueryString(authorize.Query)["state"]!;
    }

    /// <summary>
    /// The provider's redirect back, then our own callback. Two hops, because the handler
    /// owns the first and the endpoint owns the second.
    /// </summary>
    private static async Task<string> CompleteAsync(
        HttpClient browser, string provider, string state, CancellationToken ct)
    {
        var handled = await browser.GetAsync(
            $"/api/v1/auth/external/{provider}/oauth?code=fake-code&state={Uri.EscapeDataString(state)}", ct);
        if (handled.StatusCode != HttpStatusCode.Redirect)
        {
            Assert.Fail($"oauth hop: {handled.StatusCode} {await handled.Content.ReadAsStringAsync(ct)}");
        }
        Assert.Equal(ExternalAuthExtensions.CompletionPath(provider), handled.Headers.Location!.ToString());

        var completed = await browser.GetAsync(handled.Headers.Location, ct);
        if (completed.StatusCode != HttpStatusCode.Redirect)
        {
            Assert.Fail($"callback: {completed.StatusCode} {await completed.Content.ReadAsStringAsync(ct)}");
        }
        return completed.Headers.Location!.ToString();
    }

    private async Task SignInWithPasswordAsync(HttpClient browser, string email, CancellationToken ct)
    {
        var response = await browser.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, ApiTestContext.DefaultPassword), ct);
        response.EnsureSuccessStatusCode();
    }

    // Read with SQL on purpose: what is under test is what ended up in the database, not
    // what an endpoint chose to say about it. ASP.NET Identity names its own tables, so
    // these are the one place in the repo where a raw query is not snake_case - the
    // columns still are.

    /// <summary>By address, because the fixture seeds an administrator: a bare count is never zero.</summary>
    private async Task<int> CountAccountsAsync(string email, CancellationToken ct) =>
        await ScalarAsync(
            """
            SELECT count(*) FROM identity."AspNetUsers" WHERE normalized_email = upper(@email)
            """, ct, ("email", email));

    private async Task<int> CountLoginsAsync(string email, CancellationToken ct) =>
        await ScalarAsync(
            """
            SELECT count(*) FROM identity."AspNetUserLogins" l
            JOIN identity."AspNetUsers" u ON u.id = l.user_id
            WHERE u.normalized_email = upper(@email)
            """, ct, ("email", email));

    private async Task<(bool Confirmed, bool HasPassword)> ReadAccountAsync(
        string email, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT email_confirmed, password_hash IS NOT NULL
            FROM identity."AspNetUsers" WHERE normalized_email = upper(@email)
            """, connection);
        command.Parameters.AddWithValue("email", email);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private async Task ConfirmEmailAsync(string email, CancellationToken ct) =>
        await ExecuteAsync(
            """UPDATE identity."AspNetUsers" SET email_confirmed = true WHERE normalized_email = upper(@email)""",
            ct, ("email", email));

    private async Task<int> ScalarAsync(string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string, object)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}
