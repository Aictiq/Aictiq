using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// The credential flows: changing a password from inside a session, moving an email
/// address, and getting back in when the password is gone.
///
/// Three properties carry the file. A mailed link is <b>single-use</b> and the database is
/// what says so - it is spent with one conditional UPDATE, so two clicks produce one
/// change and one refusal. <c>/auth/forgot</c> <b>reveals nothing</b>: an address with an
/// account and one without get byte-identical answers. And a reset <b>ends every
/// session</b>, because the premise of a reset is that the old password may be in somebody
/// else's hands.
/// </summary>
[Trait("Category", "Auth")]
[Collection("postgres")]
public sealed class AccountRecoveryTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Email = "ada@test.local";
    private const string Password = ApiTestContext.DefaultPassword;
    private const string NewPassword = "Ada.New.Password.2026";

    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private HttpClient _anonymous = null!;
    private string _refreshToken = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "recovery");
        var auth = await _context.RegisterAsync(Email, "Ada", "Lovelace");
        _client = _context.ClientFor(auth);
        _refreshToken = auth.RefreshToken!;
        _anonymous = _context.Anonymous();
    }

    public async ValueTask DisposeAsync()
    {
        _anonymous.Dispose();
        _client.Dispose();
        await _context.DisposeAsync();
    }

    // ----------------------------------------------------------------- changing a password

    [Fact]
    public async Task changing_a_password_needs_the_current_one()
    {
        var ct = TestContext.Current.CancellationToken;

        var wrong = await _client.PostAsJsonAsync("/api/v1/me/password",
            new ChangePasswordRequest("not-the-password", NewPassword), ct);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        // Named, because the form has two password fields and "invalid" on the wrong one
        // sends people round in circles.
        Assert.Contains("currentPassword", await wrong.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The old one still works, so nothing was half-applied.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Email, Password, ct));
    }

    [Fact]
    public async Task changing_a_password_signs_out_every_other_device_but_not_this_one()
    {
        var ct = TestContext.Current.CancellationToken;

        var other = await _context.LoginAsync(Email, Password);

        var response = await _client.PostAsJsonAsync("/api/v1/me/password",
            new ChangePasswordRequest(Password, NewPassword), ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The other device's rotation chain is dead …
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(other.RefreshToken!, ct));
        // … and this one's is not, because signing out the browser that made the change
        // reads as a failure rather than as a confirmation.
        Assert.Equal(HttpStatusCode.OK, await RefreshStatusAsync(_refreshToken, ct));

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(Email, Password, ct));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Email, NewPassword, ct));
    }

    [Fact]
    public async Task a_write_scoped_token_cannot_give_itself_a_password()
    {
        var ct = TestContext.Current.CancellationToken;

        // An account with no password of its own - the provider-only case, where there is
        // nothing for the endpoint to ask the caller to prove.
        var provider = await _context.RegisterAsync("grace@test.local", "Grace", "Hopper");
        using var browser = _context.ClientFor(provider);
        await ClearPasswordAsync("grace@test.local", ct);

        var created = await browser.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("ci", ["read", "write"], null, null), ct);
        created.EnsureSuccessStatusCode();
        var secret = (await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, ct))!.Secret;

        using var narrowed = _context.Anonymous();
        narrowed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var password = await narrowed.PostAsJsonAsync("/api/v1/me/password",
            new ChangePasswordRequest(null, NewPassword), ct);
        var email = await narrowed.PostAsJsonAsync("/api/v1/me/email",
            new ChangeEmailRequest("elsewhere@test.local", null), ct);

        // Setting a password *is* minting a credential, so it needs the same admin scope
        // that minting a token does - otherwise a leaked read-write token turns itself
        // into a full login, and moving the address turns itself into the recovery path.
        Assert.Equal(HttpStatusCode.Forbidden, password.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, email.StatusCode);
        // And the account still has no password to sign in with.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync("grace@test.local", NewPassword, ct));
    }

    // -------------------------------------------------------------------- forgot and reset

    [Fact]
    public async Task an_unknown_address_is_answered_exactly_like_a_known_one()
    {
        var ct = TestContext.Current.CancellationToken;

        var known = await _anonymous.PostAsJsonAsync("/api/v1/auth/forgot",
            new ForgotPasswordRequest(Email), ct);
        var unknown = await _anonymous.PostAsJsonAsync("/api/v1/auth/forgot",
            new ForgotPasswordRequest("nobody@test.local"), ct);

        // Identical status and identical body. Anything else turns the reset form into a
        // way to ask, one address at a time, who is on this instance.
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));

        // And only one of them actually minted anything.
        Assert.Equal(1, await CountTokensAsync(ct));
    }

    [Fact]
    public async Task a_reset_link_works_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestResetAsync(ct);

        var first = await ResetAsync(token, NewPassword, ct);
        var second = await ResetAsync(token, "Another.Password.2026", ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        // The second attempt changed nothing: the password is the one the first set.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Email, NewPassword, ct));
    }

    [Fact]
    public async Task an_expired_reset_link_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestResetAsync(ct);
        await ExpireTokensAsync(ct);

        var response = await ResetAsync(token, NewPassword, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Email, Password, ct));
    }

    [Fact]
    public async Task asking_again_retires_the_link_that_was_already_sent()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await RequestResetAsync(ct);
        var second = await RequestResetAsync(ct);
        Assert.NotEqual(first, second);

        // Only the newest link works. The usual reason to ask twice is that the first one
        // went somewhere it should not have, and a link that went astray should die.
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(first, NewPassword, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(second, NewPassword, ct)).StatusCode);
    }

    [Fact]
    public async Task a_password_the_rules_refuse_does_not_spend_the_link()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestResetAsync(ct);

        var tooShort = await ResetAsync(token, "short", ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // Validating after consuming would burn a one-time link on a password that was
        // never going to be accepted, and leave the person locked out until they asked
        // for another mail.
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, NewPassword, ct)).StatusCode);
    }

    [Fact]
    public async Task a_reset_ends_every_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await _context.LoginAsync(Email, Password);
        var token = await RequestResetAsync(ct);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, NewPassword, ct)).StatusCode);

        // Every one, including the browser that asked: the premise is that the old
        // password may be in somebody else's hands, and so may the sessions it opened.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(other.RefreshToken!, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(_refreshToken, ct));
    }

    [Fact]
    public async Task a_reset_clears_a_lockout()
    {
        var ct = TestContext.Current.CancellationToken;

        // Five wrong guesses is the lockout budget; the sixth attempt is refused outright.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginStatusAsync(Email, "wrong-password-entirely", ct);
        }
        Assert.Equal(HttpStatusCode.Locked, await LoginStatusAsync(Email, Password, ct));

        var token = await RequestResetAsync(ct);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, NewPassword, ct)).StatusCode);

        // Someone who forgot their password has usually just failed to guess it. Leaving
        // the lockout would make the reset appear not to have worked.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Email, NewPassword, ct));
    }

    [Fact]
    public async Task only_the_hash_of_a_link_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestResetAsync(ct);

        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT token_hash FROM identity.user_security_tokens", connection);
        var stored = (string)(await command.ExecuteScalarAsync(ct))!;

        // Straight to the table: a leaked database must hand out no working links, and no
        // endpoint could prove that by declining to show one.
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.Equal(Modules.Identity.Domain.UserSecurityToken.Hash(token), stored);
    }

    // ------------------------------------------------------------------ changing an address

    [Fact]
    public async Task a_new_address_takes_effect_only_once_its_own_inbox_confirms_it()
    {
        var ct = TestContext.Current.CancellationToken;

        var token = await RequestEmailChangeAsync("augusta@test.local", ct);

        // Nothing has changed yet - the address is on the token, not on the account.
        var pending = await _client.GetFromJsonAsync<ProfileView>("/api/v1/me", ApiTestContext.Json, ct);
        Assert.Equal(Email, pending!.Email);
        Assert.Equal("augusta@test.local", pending.PendingEmail);

        var confirmed = await _anonymous.PostAsJsonAsync("/api/v1/auth/email-change",
            new ConfirmEmailChangeRequest(token), ct);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var after = await _client.GetFromJsonAsync<ProfileView>("/api/v1/me", ApiTestContext.Json, ct);
        Assert.Equal("augusta@test.local", after!.Email);
        Assert.Null(after.PendingEmail);
        // The link proved control of the mailbox, which is the whole question.
        Assert.True(after.EmailConfirmed);

        // And it is the address that now signs in.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync("augusta@test.local", Password, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(Email, Password, ct));
    }

    [Fact]
    public async Task changing_an_address_needs_the_password()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync("/api/v1/me/email",
            new ChangeEmailRequest("augusta@test.local", null), ct);

        // An unattended browser must not be a way to move where recovery mail goes.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("currentPassword", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task an_address_somebody_else_already_uses_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await _context.RegisterAsync("grace@test.local", "Grace", "Hopper");

        var response = await _client.PostAsJsonAsync("/api/v1/me/email",
            new ChangeEmailRequest("grace@test.local", Password), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_confirmation_link_works_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestEmailChangeAsync("augusta@test.local", ct);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(token, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(token, ct)).StatusCode);
    }

    [Fact]
    public async Task a_pending_change_can_be_called_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await RequestEmailChangeAsync("augusta@test.local", ct);

        var cancelled = await _client.DeleteAsync("/api/v1/me/email", ct);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(token, ct)).StatusCode);
        var profile = await _client.GetFromJsonAsync<ProfileView>("/api/v1/me", ApiTestContext.Json, ct);
        Assert.Null(profile!.PendingEmail);
        Assert.Equal(Email, profile.Email);
    }

    // ---------------------------------------------------------------------------- helpers

    /// <summary>
    /// Asks for a reset and digs the link out of the outbox row the request wrote.
    ///
    /// Reading it there rather than from a mailbox is the point twice over: the token is
    /// never returned by any endpoint, and the row being present at all is the proof that
    /// the mail and the token committed together.
    /// </summary>
    private async Task<string> RequestResetAsync(CancellationToken ct)
    {
        var response = await _anonymous.PostAsJsonAsync("/api/v1/auth/forgot",
            new ForgotPasswordRequest(Email), ct);
        response.EnsureSuccessStatusCode();
        return await LastLinkTokenAsync("resetUrl", ct);
    }

    private async Task<string> RequestEmailChangeAsync(string address, CancellationToken ct)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/me/email",
            new ChangeEmailRequest(address, Password), ct);
        response.EnsureSuccessStatusCode();
        return await LastLinkTokenAsync("confirmUrl", ct);
    }

    private async Task<string> LastLinkTokenAsync(string variable, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT payload -> 'Variables' ->> '{variable}'
            FROM shared.outbox_messages
            WHERE payload -> 'Variables' ? '{variable}'
            ORDER BY occurred_at DESC
            LIMIT 1
            """, connection);

        var url = (string?)await command.ExecuteScalarAsync(ct);
        Assert.NotNull(url);
        return url![(url.LastIndexOf('/') + 1)..];
    }

    private Task<HttpResponseMessage> ResetAsync(string token, string password, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/reset", new ResetPasswordRequest(token, password), ct);

    private Task<HttpResponseMessage> ConfirmAsync(string token, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/email-change", new ConfirmEmailChangeRequest(token), ct);

    private async Task<HttpStatusCode> LoginStatusAsync(string email, string password, CancellationToken ct)
    {
        using var client = _context.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), ct);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> RefreshStatusAsync(string refreshToken, CancellationToken ct)
    {
        using var client = _context.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(refreshToken), ct);
        return response.StatusCode;
    }

    private async Task<long> CountTokensAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM identity.user_security_tokens", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Makes an account look as though it had only ever signed in with a provider. There
    /// is no endpoint for it, because arriving at that state is something registration
    /// does, not something a person asks for.
    /// </summary>
    private async Task ClearPasswordAsync(string email, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE identity."AspNetUsers" SET password_hash = NULL WHERE normalized_email = @email
            """, connection);
        command.Parameters.AddWithValue("email", email.ToUpperInvariant());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }

    /// <summary>Backdating is the only way to age a link in a test.</summary>
    private async Task ExpireTokensAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE identity.user_security_tokens SET expires_at = now() - interval '1 hour'", connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
