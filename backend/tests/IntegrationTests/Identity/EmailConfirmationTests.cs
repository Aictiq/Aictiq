using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel;

namespace Aictiq.IntegrationTests.Identity;

/// <summary>
/// A password account on an instance that can send mail has to prove its address before
/// it can sign in - unless it registered from an invitation mailed to that very address,
/// in which case the invitation already proved it.
///
/// The instance under test is configured to send (the relay is never dialled: the API only
/// queues through the outbox, and Workers is not running), so the links are read from the
/// outbox row the request wrote, exactly as <c>AccountRecoveryTests</c> does.
/// </summary>
[Trait("Category", "Auth")]
public sealed class EmailConfirmationTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Password = ApiTestContext.DefaultPassword;
    private const string Acme = "acme";

    private ApiTestContext _context = null!;
    private HttpClient _anonymous = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "email_confirm", settings =>
        {
            settings["Email:Smtp:Host"] = "smtp.invalid";
            settings["Email:FromAddress"] = "aictiq@test.local";
            settings["Email:BaseUrl"] = "https://aictiq.test";
        });
        _anonymous = _context.Anonymous();

        (await _context.Admin.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _anonymous.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task a_registration_waits_for_its_confirmation_link_before_it_can_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "newadmin@test.local";

        var registered = await RegisterAsync(email, ct: ct);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var pending = await registered.Content.ReadFromJsonAsync<RegistrationPending>(ct);
        Assert.Equal(email, pending!.Email);
        // No session of any kind on a 202.
        Assert.False(registered.Headers.Contains("Set-Cookie"));

        var blocked = await LoginAsync(email, Password, ct);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal(ProblemTypes.EmailUnconfirmed, problem!.Type);

        // The unconfirmed answer is only given to the password's holder.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, "Wrong.Password.123", ct)).StatusCode);

        var token = await LastConfirmationTokenAsync(email, ct);
        var verified = await VerifyAsync(token, ct);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.Equal(email, (await verified.Content.ReadFromJsonAsync<EmailVerified>(ct))!.Email);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, Password, ct)).StatusCode);

        // A second click - or a mail scanner that got there first - still reads as done.
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(token, ct)).StatusCode);
    }

    [Fact]
    public async Task a_made_up_confirmation_link_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await VerifyAsync("not-a-real-token", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task registering_from_an_invitation_to_the_same_address_is_already_confirmed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "invited@test.local";
        var link = await InviteAsync(email, ct);

        // Case and whitespace are not a different address.
        var registered = await RegisterAsync(" Invited@Test.local ", invitationToken: link.Token, ct: ct);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var auth = await registered.Content.ReadFromJsonAsync<AuthResponse>(ct);
        Assert.NotEmpty(auth!.AccessToken!);
        Assert.Null(await LastConfirmationUrlAsync(email, ct));

        using var client = _context.ClientFor(auth);
        (await client.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, Password, ct)).StatusCode);
    }

    [Fact]
    public async Task a_forwarded_invitation_confirms_by_mail_and_the_link_leads_back_to_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync("original@test.local", ct);

        var registered = await RegisterAsync(
            "forwardee@test.local", invitationToken: link.Token, next: $"/invite/{link.Token}", ct: ct);

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var url = await LastConfirmationUrlAsync("forwardee@test.local", ct);
        Assert.StartsWith("https://aictiq.test/verify-email/", url, StringComparison.Ordinal);
        Assert.EndsWith($"?next={Uri.EscapeDataString($"/invite/{link.Token}")}", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("/\\evil.example")]
    public async Task a_next_that_leaves_the_site_is_dropped_from_the_link(string next)
    {
        var ct = TestContext.Current.CancellationToken;
        var email = $"next{Math.Abs(next.GetHashCode())}@test.local";

        (await RegisterAsync(email, next: next, ct: ct)).EnsureSuccessStatusCode();

        var url = await LastConfirmationUrlAsync(email, ct);
        Assert.DoesNotContain("next=", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_spent_or_revoked_invitation_confirms_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "revoked@test.local";
        var link = await InviteAsync(email, ct);
        (await _context.Admin.DeleteAsync(
            $"/api/v1/orgs/{Acme}/invitations/{link.Invitation.Id}", ct)).EnsureSuccessStatusCode();

        var registered = await RegisterAsync(email, invitationToken: link.Token, ct: ct);

        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
    }

    [Fact]
    public async Task resending_replaces_the_link_and_answers_the_same_for_any_address()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "resend@test.local";
        (await RegisterAsync(email, ct: ct)).EnsureSuccessStatusCode();
        var first = await LastConfirmationTokenAsync(email, ct);

        // Inside the cooldown: accepted, but nothing new is minted.
        var early = await ResendAsync(email, ct);
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        Assert.Equal(first, await LastConfirmationTokenAsync(email, ct));

        await AgeConfirmationTokensAsync(email, ct);
        var resent = await ResendAsync(email, ct);
        var unknown = await ResendAsync("nobody-here@test.local", ct);
        Assert.Equal(HttpStatusCode.Accepted, resent.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(
            await resent.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));

        var second = await LastConfirmationTokenAsync(email, ct);
        Assert.NotEqual(first, second);

        // Only the newest link works.
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(first, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(second, ct)).StatusCode);
    }

    [Fact]
    public async Task a_password_reset_link_also_confirms_the_address()
    {
        var ct = TestContext.Current.CancellationToken;
        const string email = "lostmail@test.local";
        const string newPassword = "Brand.New.Password.2026";
        (await RegisterAsync(email, ct: ct)).EnsureSuccessStatusCode();

        (await _anonymous.PostAsJsonAsync("/api/v1/auth/forgot", new ForgotPasswordRequest(email), ct))
            .EnsureSuccessStatusCode();
        var resetToken = await LastLinkTokenAsync("resetUrl", email, ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _anonymous.PostAsJsonAsync("/api/v1/auth/reset",
            new ResetPasswordRequest(resetToken, newPassword), ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, newPassword, ct)).StatusCode);
    }

    [Fact]
    public async Task the_seeded_administrator_is_not_asked_to_confirm()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task without_turnstile_keys_the_challenge_endpoint_offers_no_widget()
    {
        var ct = TestContext.Current.CancellationToken;

        var info = await _anonymous.GetFromJsonAsync<AuthChallengeInfo>("/api/v1/auth/challenge", ct);

        Assert.Null(info!.TurnstileSiteKey);
    }

    // ---------------------------------------------------------------------------- helpers

    private Task<HttpResponseMessage> RegisterAsync(
        string email, string? invitationToken = null, string? next = null, CancellationToken ct = default) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, Password, "New", "Person", invitationToken, next), ct);

    private Task<HttpResponseMessage> LoginAsync(string email, string password, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), ct);

    private Task<HttpResponseMessage> VerifyAsync(string token, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token), ct);

    private Task<HttpResponseMessage> ResendAsync(string email, CancellationToken ct) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/verify-email/resend", new ResendConfirmationRequest(email), ct);

    private async Task<InvitationLink> InviteAsync(string email, CancellationToken ct)
    {
        var response = await _context.Admin.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest(email, OrgRole.Member, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;
    }

    private Task<string?> LastConfirmationUrlAsync(string email, CancellationToken ct) =>
        LastLinkAsync("confirmUrl", email, ct);

    private async Task<string> LastConfirmationTokenAsync(string email, CancellationToken ct) =>
        await LastLinkTokenAsync("confirmUrl", email, ct);

    private async Task<string> LastLinkTokenAsync(string variable, string email, CancellationToken ct)
    {
        var url = await LastLinkAsync(variable, email, ct);
        Assert.NotNull(url);
        var path = url!.Split('?')[0];
        return path[(path.LastIndexOf('/') + 1)..];
    }

    private async Task<string?> LastLinkAsync(string variable, string email, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload -> 'Variables' ->> @variable
            FROM shared.outbox_messages
            WHERE payload -> 'Variables' ? @variable AND lower(payload ->> 'To') = lower(@email)
            ORDER BY occurred_at DESC
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("variable", variable);
        command.Parameters.AddWithValue("email", email);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    /// <summary>Moves the account's confirmation links out of the one-minute resend cooldown.</summary>
    private async Task AgeConfirmationTokensAsync(string email, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE identity.user_security_tokens t SET created_at = t.created_at - interval '5 minutes'
            FROM identity."AspNetUsers" u
            WHERE t.user_id = u.id AND u.normalized_email = upper(@email) AND t.purpose = 2
            """, connection);
        command.Parameters.AddWithValue("email", email);
        await command.ExecuteNonQueryAsync(ct);
    }
}
