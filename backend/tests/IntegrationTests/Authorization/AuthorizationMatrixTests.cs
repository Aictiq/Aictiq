using System.Net;
using System.Net.Http.Json;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.IntegrationTests.Storage;

namespace Aictiq.IntegrationTests.Authorization;

[Collection("postgres")]
public sealed class AuthorizationMatrixTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "authz_matrix");
        _alice = _context.ClientFor(await _context.RegisterAsync("alice@test.local", "Alice"));
        _bob = _context.ClientFor(await _context.RegisterAsync("bob@test.local", "Bob"));
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _bob.Dispose();
        await _context.DisposeAsync();
    }

    [Theory]
    [InlineData("GET", "/api/v1/auth/me")]
    [InlineData("GET", "/api/v1/admin/users")]
    [InlineData("GET", "/api/v1/me")]
    [InlineData("GET", "/api/v1/me/sessions")]
    public async Task anonymous_requests_get_401(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _context.Anonymous();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task regular_users_get_403_on_admin_surfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var users = await _alice.GetAsync("/api/v1/admin/users", ct);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);

    }

    [Fact]
    public async Task deactivating_a_user_revokes_their_sessions_and_blocks_login()
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync("victim@test.local");

        var deactivate = await _context.Admin.PostAsync(
            $"/api/v1/admin/users/{auth.User.Id}/deactivate", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        using var anonymous = _context.Anonymous();
        var refresh = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(auth.RefreshToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("victim@test.local", ApiTestContext.DefaultPassword), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
