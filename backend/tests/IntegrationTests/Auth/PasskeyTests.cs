using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// Passkeys from both ends: added, renamed and removed in a session, and used to sign in
/// from nothing. The authenticator is <see cref="FakeAuthenticator"/>, which answers the
/// API's own options with real signatures, so Identity's WebAuthn checks run unaltered.
/// </summary>
[Trait("Category", "Auth")]
public sealed class PasskeyTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "passkeys");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task a_passkey_is_added_signed_in_with_renamed_and_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "keyholder@test.local";
        using var client = await RegisterAsync(email);
        using var authenticator = new FakeAuthenticator();

        var added = await AddAsync(client, authenticator, "Laptop", ct);
        Assert.Equal(authenticator.Id, added.Id);
        Assert.Equal("Laptop", added.Name);
        Assert.Equal(1, await NoticesAsync(email, "Passkey added", ct));

        var auth = await SignInAsync(authenticator, ct);
        Assert.Equal(email, auth.User.Email);
        using var signedIn = _context.ClientFor(auth);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/session", ct)).StatusCode);

        var renamed = await client.PatchAsJsonAsync($"/api/v1/me/passkeys/{added.Id}",
            new RenamePasskeyRequest("  Work laptop  "), ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var listed = await client.GetFromJsonAsync<List<PasskeySummary>>("/api/v1/me/passkeys", ct);
        Assert.Equal("Work laptop", Assert.Single(listed!).Name);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/me/passkeys/{added.Id}", ct)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<PasskeySummary>>("/api/v1/me/passkeys", ct))!);
        Assert.Equal(1, await NoticesAsync(email, "Passkey removed", ct));

        // A removed passkey is no way in.
        var (ticket, options) = await LoginOptionsAsync(ct);
        using var anonymous = Anonymous();
        var refused = await anonymous.PostAsJsonAsync("/api/v1/auth/passkey/login",
            new PasskeyLoginRequest(ticket, authenticator.Get(options)), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task a_passkey_sign_in_does_not_ask_for_the_second_factor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "both@test.local";
        using var client = await RegisterAsync(email);
        var setup = await (await client.PostAsync("/api/v1/me/two-factor/setup", null, ct))
            .Content.ReadFromJsonAsync<TwoFactorSetup>(ct);
        (await client.PostAsJsonAsync("/api/v1/me/two-factor/enable",
            new EnableTwoFactorRequest(TwoFactorTests.Totp(setup!.SharedKey)), ct)).EnsureSuccessStatusCode();
        using var authenticator = new FakeAuthenticator();
        await AddAsync(client, authenticator, "Phone", ct);

        var auth = await SignInAsync(authenticator, ct);

        Assert.Equal(email, auth.User.Email);
        Assert.NotNull(auth.AccessToken);
    }

    [Fact]
    public async Task a_sign_in_ticket_works_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await RegisterAsync("replay@test.local");
        using var authenticator = new FakeAuthenticator();
        await AddAsync(client, authenticator, "Key", ct);
        var (ticket, options) = await LoginOptionsAsync(ct);
        var assertion = authenticator.Get(options);
        using var anonymous = Anonymous();

        var first = await anonymous.PostAsJsonAsync("/api/v1/auth/passkey/login",
            new PasskeyLoginRequest(ticket, assertion), ct);
        var replay = await anonymous.PostAsJsonAsync("/api/v1/auth/passkey/login",
            new PasskeyLoginRequest(ticket, assertion), ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(ProblemTypes.SignInExpired,
            (await replay.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Type);
    }

    [Fact]
    public async Task someone_else_cannot_rename_or_remove_your_passkey()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await RegisterAsync("owner.key@test.local");
        using var other = await RegisterAsync("other.key@test.local");
        using var authenticator = new FakeAuthenticator();
        var added = await AddAsync(owner, authenticator, "Mine", ct);

        Assert.Equal(HttpStatusCode.NotFound, (await other.PatchAsJsonAsync(
            $"/api/v1/me/passkeys/{added.Id}", new RenamePasskeyRequest("Theirs"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/me/passkeys/{added.Id}", ct)).StatusCode);
        Assert.Equal("Mine", Assert.Single((await owner.GetFromJsonAsync<List<PasskeySummary>>("/api/v1/me/passkeys", ct))!).Name);
    }

    [Fact]
    public async Task a_registration_ticket_is_only_good_for_the_account_that_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var asker = await RegisterAsync("asker@test.local");
        using var thief = await RegisterAsync("thief@test.local");
        using var authenticator = new FakeAuthenticator();
        var options = (await (await asker.PostAsync("/api/v1/me/passkeys/options", null, ct))
            .Content.ReadFromJsonAsync<PasskeyOptions>(ct))!;

        var response = await thief.PostAsJsonAsync("/api/v1/me/passkeys",
            new AddPasskeyRequest(options.Ticket, authenticator.Create(options.Options), "Stolen"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await thief.GetFromJsonAsync<List<PasskeySummary>>("/api/v1/me/passkeys", ct))!);
    }

    // ---------------------------------------------------------------------------- helpers

    /// <summary>A browser always sends Origin on these requests, and Identity checks it against the client data.</summary>
    private HttpClient Anonymous()
    {
        var client = _context.Anonymous();
        client.DefaultRequestHeaders.Add("Origin", FakeAuthenticator.Origin);
        return client;
    }

    private async Task<HttpClient> RegisterAsync(string email)
    {
        var client = _context.ClientFor(await _context.RegisterAsync(email));
        client.DefaultRequestHeaders.Add("Origin", FakeAuthenticator.Origin);
        return client;
    }

    private static async Task<PasskeySummary> AddAsync(
        HttpClient client, FakeAuthenticator authenticator, string name, CancellationToken ct)
    {
        var optionsResponse = await client.PostAsync("/api/v1/me/passkeys/options", null, ct);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        var options = (await optionsResponse.Content.ReadFromJsonAsync<PasskeyOptions>(ct))!;

        var response = await client.PostAsJsonAsync("/api/v1/me/passkeys",
            new AddPasskeyRequest(options.Ticket, authenticator.Create(options.Options), name), ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<PasskeySummary>(ct))!;
    }

    private async Task<(string Ticket, System.Text.Json.JsonElement Options)> LoginOptionsAsync(CancellationToken ct)
    {
        using var anonymous = Anonymous();
        var response = await anonymous.PostAsync("/api/v1/auth/passkey/options", null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = (await response.Content.ReadFromJsonAsync<PasskeyOptions>(ct))!;
        return (options.Ticket, options.Options);
    }

    private async Task<AuthResponse> SignInAsync(FakeAuthenticator authenticator, CancellationToken ct)
    {
        var (ticket, options) = await LoginOptionsAsync(ct);
        using var anonymous = Anonymous();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/passkey/login",
            new PasskeyLoginRequest(ticket, authenticator.Get(options)), ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(ct))!;
    }

    private async Task<long> NoticesAsync(string email, string headline, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM shared.outbox_messages
            WHERE payload ->> 'Template' = @template
              AND payload ->> 'To' = @email
              AND payload -> 'Variables' ->> 'headline' = @headline
            """, connection);
        command.Parameters.AddWithValue("template", SecurityNotices.Template);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("headline", headline);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
