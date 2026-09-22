using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Identity;

[Trait("Category", "Auth")]
[Collection("postgres")]
public sealed class AuthFlowTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "auth_flow");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task register_returns_tokens_and_me_works_with_the_access_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("register@test.local", "Rina", "Novak");

        // Bearer mode (no X-Aictiq-Request header) still returns the tokens in the body.
        Assert.NotEmpty(auth.AccessToken!);
        Assert.NotEmpty(auth.RefreshToken!);
        Assert.Equal("register@test.local", auth.User.Email);
        Assert.Contains(Aictiq.SharedKernel.Roles.User, auth.User.Roles);

        using var client = _context.ClientFor(auth);
        var me = await client.GetFromJsonAsync<UserInfo>("/api/v1/auth/me", ct);
        Assert.Equal(auth.User.Id, me!.Id);
        Assert.Equal("Rina", me.FirstName);
    }

    [Fact]
    public async Task password_below_twelve_characters_is_rejected_as_a_field_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("shortpw@test.local", "Short.1", "Test", "User"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("password", problem!.Errors.Keys);
    }

    [Fact]
    public async Task unknown_email_and_wrong_password_return_indistinguishable_401s()
    {
        var ct = TestContext.Current.CancellationToken;
        await _context.RegisterAsync("enum-probe@test.local");

        using var client = _context.Anonymous();
        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("nobody@test.local", "Wrong.Password.1"), ct);
        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("enum-probe@test.local", "Wrong.Password.1"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        // Same title, same status - nothing to tell an attacker which emails exist.
        // (Bodies differ only by traceId, which varies per request by design.)
        var first = await unknownUser.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        var second = await wrongPassword.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal(first!.Title, second!.Title);
        Assert.Equal(first.Status, second.Status);
    }

    [Fact]
    public async Task refresh_rotates_the_token_and_reuse_kills_the_whole_family()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("rotation@test.local");
        using var client = _context.Anonymous();

        // Legitimate rotation: old token is consumed, a new one comes back.
        var firstRefresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(auth.RefreshToken), ct);
        firstRefresh.EnsureSuccessStatusCode();
        var rotated = await firstRefresh.Content.ReadFromJsonAsync<AuthResponse>(ct);
        Assert.NotEqual(auth.RefreshToken, rotated!.RefreshToken);

        // Replaying the consumed token is theft-or-bug; either way the family dies.
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(auth.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The descendant issued moments ago is dead too - that is the family revocation.
        var descendant = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(rotated.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);
    }

    [Fact]
    public async Task concurrent_refreshes_of_one_token_produce_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("refresh-race@test.local");

        // The whole point of rotation is that a token can be spent once. A read-check-write
        // in TokenService would let every one of these pass and silently fork the family,
        // permanently disabling reuse detection for this user.
        var clients = Enumerable.Range(0, 8).Select(_ => _context.Anonymous()).ToArray();
        try
        {
            var responses = await Task.WhenAll(clients.Select(client =>
                client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(auth.RefreshToken), ct)));

            Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
            Assert.All(responses.Where(r => !r.IsSuccessStatusCode),
                r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

            // The losers are treated as reuse, so the family - including the token the
            // winner just received - is revoked.
            var winner = responses.Single(r => r.IsSuccessStatusCode);
            var rotated = await winner.Content.ReadFromJsonAsync<AuthResponse>(ct);
            using var anonymous = _context.Anonymous();
            var descendant = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
                new RefreshRequest(rotated!.RefreshToken), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, descendant.StatusCode);

            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task logout_revokes_the_refresh_token_family()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("logout@test.local");

        using var client = _context.ClientFor(auth);
        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout",
            new RefreshRequest(auth.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var anonymous = _context.Anonymous();
        var afterLogout = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(auth.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task five_failed_attempts_lock_the_account_even_for_the_correct_password()
    {
        var ct = TestContext.Current.CancellationToken;
        await _context.RegisterAsync("lockout@test.local");
        using var client = _context.Anonymous();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest("lockout@test.local", "Wrong.Password.1"), ct);
        }

        var lockedOut = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("lockout@test.local", ApiTestContext.DefaultPassword), ct);
        Assert.Equal(HttpStatusCode.Locked, lockedOut.StatusCode);
    }
}
