using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// Two-factor authentication with an authenticator app, from switching it on to signing in
/// with it - and everything it must leave alone: a password-only account, and the personal
/// access tokens the CLI and agents use.
///
/// Codes are computed here with the same RFC 6238 arithmetic an authenticator app uses,
/// from the shared key the setup endpoint shows. Nothing is stubbed on the server side.
/// </summary>
[Trait("Category", "Auth")]
public sealed class TwoFactorTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Password = ApiTestContext.DefaultPassword;

    private ApiTestContext _context = null!;
    private HttpClient _anonymous = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "two_factor");
        _anonymous = _context.Anonymous();
    }

    public async ValueTask DisposeAsync()
    {
        _anonymous.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task an_account_without_two_factor_signs_in_with_its_password_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        await _context.RegisterAsync("plain@test.local");

        var response = await LoginAsync("plain@test.local", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await response.Content.ReadFromJsonAsync<AuthResponse>(ct))!.AccessToken);
    }

    [Fact]
    public async Task turning_it_on_needs_a_code_from_the_app_and_hands_out_ten_recovery_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await RegisterAsync("enable@test.local");

        var setup = await StartSetupAsync(client, ct);
        Assert.StartsWith("otpauth://totp/Aictiq:enable%40test.local?secret=", setup.AuthenticatorUri, StringComparison.Ordinal);

        var wrong = await client.PostAsJsonAsync("/api/v1/me/two-factor/enable",
            new EnableTwoFactorRequest("000000"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False((await StatusAsync(client, ct)).Enabled);

        var codes = await EnableAsync(client, setup, ct);

        Assert.Equal(TwoFactorEndpoints.RecoveryCodeCount, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        var status = await StatusAsync(client, ct);
        Assert.True(status.Enabled);
        Assert.Equal(10, status.RecoveryCodesLeft);
        Assert.Equal(1, await NoticesAsync("enable@test.local", "Two-factor authentication turned on", ct));
    }

    [Fact]
    public async Task with_two_factor_on_the_password_alone_issues_no_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "second@test.local";
        using var client = await RegisterAsync(email);
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);

        var first = await LoginAsync(email, ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("accessToken", body, StringComparison.Ordinal);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        var challenge = (await first.Content.ReadFromJsonAsync<TwoFactorChallenge>(ct))!;
        Assert.True(challenge.TwoFactorRequired);

        var second = await SecondStepAsync(challenge.Ticket, Totp(setup.SharedKey), null, ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var auth = (await second.Content.ReadFromJsonAsync<AuthResponse>(ct))!;
        Assert.Equal(email, auth.User.Email);
        using var signedIn = _context.ClientFor(auth);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/v1/auth/session", ct)).StatusCode);

        // The ticket is spent by the code that worked.
        var again = await SecondStepAsync(challenge.Ticket, Totp(setup.SharedKey), null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(ProblemTypes.SignInExpired,
            (await again.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Type);
    }

    [Fact]
    public async Task the_cookie_flow_sets_cookies_only_after_the_code()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "cookie2fa@test.local";
        using var client = await RegisterAsync(email);
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);
        using var browser = _context.Browser();

        var first = await browser.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        var challenge = (await first.Content.ReadFromJsonAsync<TwoFactorChallenge>(ct))!;

        var second = await browser.PostAsJsonAsync("/api/v1/auth/login/two-factor",
            new TwoFactorLoginRequest(challenge.Ticket, Totp(setup.SharedKey), null), ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains(second.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(AuthCookies.AccessCookieName + "=", StringComparison.Ordinal));
        Assert.Null((await second.Content.ReadFromJsonAsync<AuthResponse>(ct))!.AccessToken);
    }

    [Fact]
    public async Task a_recovery_code_works_once_and_a_new_set_retires_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "recovery2fa@test.local";
        using var client = await RegisterAsync(email);
        var setup = await StartSetupAsync(client, ct);
        var codes = await EnableAsync(client, setup, ct);

        // Lower case and without the dash, as copied off paper.
        var typed = codes[0].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        Assert.Equal(HttpStatusCode.OK, (await SecondStepAsync(await TicketAsync(email, ct), null, typed, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SecondStepAsync(await TicketAsync(email, ct), null, codes[0], ct)).StatusCode);
        Assert.Equal(9, (await StatusAsync(client, ct)).RecoveryCodesLeft);
        Assert.Equal(1, await NoticesAsync(email, "Recovery code used", ct));

        var regenerated = await client.PostAsJsonAsync("/api/v1/me/two-factor/recovery-codes",
            new TwoFactorProofRequest(Password, null), ct);
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        var fresh = (await regenerated.Content.ReadFromJsonAsync<RecoveryCodes>(ct))!.Codes;

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SecondStepAsync(await TicketAsync(email, ct), null, codes[1], ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SecondStepAsync(await TicketAsync(email, ct), null, fresh[0], ct)).StatusCode);
        Assert.Equal(1, await NoticesAsync(email, "New recovery codes", ct));
    }

    [Fact]
    public async Task wrong_codes_lock_the_account_like_wrong_passwords()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "guesser@test.local";
        using var client = await RegisterAsync(email);
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);
        var ticket = await TicketAsync(email, ct);
        var wrong = WrongCode(setup.SharedKey);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SecondStepAsync(ticket, wrong, null, ct)).StatusCode);
        }

        Assert.Equal((HttpStatusCode)423, (await SecondStepAsync(ticket, wrong, null, ct)).StatusCode);
        // Locked means locked: the right code does not get through either.
        Assert.Equal((HttpStatusCode)423, (await SecondStepAsync(ticket, Totp(setup.SharedKey), null, ct)).StatusCode);
    }

    [Fact]
    public async Task a_made_up_ticket_is_an_expired_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SecondStepAsync("not-a-ticket", "123456", null, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ProblemTypes.SignInExpired,
            (await response.Content.ReadFromJsonAsync<ProblemDetails>(ct))!.Type);
    }

    [Fact]
    public async Task turning_it_off_needs_proof_and_brings_back_password_only_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "disable@test.local";
        using var client = await RegisterAsync(email);
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/me/two-factor/disable",
            new TwoFactorProofRequest(null, null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/me/two-factor/disable",
            new TwoFactorProofRequest("Not.The.Password.1", null), ct)).StatusCode);
        Assert.True((await StatusAsync(client, ct)).Enabled);

        var disabled = await client.PostAsJsonAsync("/api/v1/me/two-factor/disable",
            new TwoFactorProofRequest(null, Totp(setup.SharedKey)), ct);

        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.False((await StatusAsync(client, ct)).Enabled);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, ct)).StatusCode);
        Assert.Equal(1, await NoticesAsync(email, "Two-factor authentication turned off", ct));
    }

    [Fact]
    public async Task setting_it_up_twice_is_refused_while_it_is_on()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await RegisterAsync("twice@test.local");
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);

        var again = await client.PostAsync("/api/v1/me/two-factor/setup", null, ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        // The running authenticator still works.
        Assert.Equal(HttpStatusCode.OK, (await SecondStepAsync(
            await TicketAsync("twice@test.local", ct), Totp(setup.SharedKey), null, ct)).StatusCode);
    }

    [Fact]
    public async Task personal_access_tokens_keep_working_with_two_factor_on()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await RegisterAsync("cli2fa@test.local");
        var created = await client.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("cli", null, null, null), ApiTestContext.Json, ct);
        created.EnsureSuccessStatusCode();
        var secret = (await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, ct))!.Secret;

        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);

        using var cli = _context.Anonymous();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await cli.GetAsync("/api/v1/auth/session", ct)).StatusCode);
    }

    [Fact]
    public async Task a_narrowed_token_cannot_turn_two_factor_off()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await RegisterAsync("narrow2fa@test.local");
        var setup = await StartSetupAsync(client, ct);
        await EnableAsync(client, setup, ct);
        var created = await client.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("ci", ["read", "write"], null, null), ApiTestContext.Json, ct);
        var secret = (await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, ct))!.Secret;

        using var cli = _context.Anonymous();
        cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var response = await cli.PostAsJsonAsync("/api/v1/me/two-factor/disable",
            new TwoFactorProofRequest(Password, null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True((await StatusAsync(client, ct)).Enabled);
    }

    // ---------------------------------------------------------------------------- helpers

    private async Task<HttpClient> RegisterAsync(string email) =>
        _context.ClientFor(await _context.RegisterAsync(email));

    private Task<HttpResponseMessage> LoginAsync(string email, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct);

    private async Task<string> TicketAsync(string email, CancellationToken ct)
    {
        var response = await LoginAsync(email, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TwoFactorChallenge>(ct))!.Ticket;
    }

    private Task<HttpResponseMessage> SecondStepAsync(
        string ticket, string? code, string? recoveryCode, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/login/two-factor",
            new TwoFactorLoginRequest(ticket, code, recoveryCode), ct);

    private static async Task<TwoFactorSetup> StartSetupAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.PostAsync("/api/v1/me/two-factor/setup", null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TwoFactorSetup>(ct))!;
    }

    private static async Task<IReadOnlyList<string>> EnableAsync(
        HttpClient client, TwoFactorSetup setup, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/v1/me/two-factor/enable",
            new EnableTwoFactorRequest(Totp(setup.SharedKey)), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecoveryCodes>(ct))!.Codes;
    }

    private static async Task<TwoFactorStatus> StatusAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<TwoFactorStatus>("/api/v1/me/two-factor", ct))!;

    /// <summary>Security mails queued for an address with this headline, read from the outbox.</summary>
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

    /// <summary>RFC 6238 with the authenticator-app defaults: SHA-1, 30 seconds, six digits.</summary>
    internal static string Totp(string sharedKey, long offset = 0)
    {
        var key = Base32(sharedKey.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant());
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(key, counter);
        var at = hash[^1] & 0x0f;
        var value = ((hash[at] & 0x7f) << 24) | (hash[at + 1] << 16) | (hash[at + 2] << 8) | hash[at + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A six-digit code that no step in the server's tolerance window produces.</summary>
    private static string WrongCode(string sharedKey)
    {
        var valid = Enumerable.Range(-3, 7).Select(o => Totp(sharedKey, o)).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
            .First(c => !valid.Contains(c));
    }

    private static byte[] Base32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in input.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
