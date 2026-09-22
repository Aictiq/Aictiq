using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// Personal access tokens: the credential the CLI, the MCP server and agents present.
///
/// Two properties carry this ticket. The secret is <b>never stored</b> - proved against
/// the table with SQL rather than against an endpoint that could simply decline to show
/// it. And a scope <b>narrows</b>: a read-only token can do strictly less than its owner,
/// never more, and every refusal it earns is a 403 rather than a surprise.
/// </summary>
[Trait("Category", "Auth")]
public sealed class AccessTokenTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _browser = null!;
    private string _userId = null!;
    private const string Acme = "acme";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _context = await ApiTestContext.CreateAsync(postgres, garage, "access_tokens");

        var auth = await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");
        _browser = _context.ClientFor(auth);
        _userId = auth.User.Id;

        var created = await _browser.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), ct);
        created.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.Dispose();
        await _context.DisposeAsync();
    }

    // ------------------------------------------------------------------------- the secret

    [Fact]
    public async Task the_secret_is_returned_once_and_never_stored()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync("laptop CLI", [], ct: ct);

        Assert.StartsWith("aiq_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(44, created.Secret.Length);

        // Straight to the table: an endpoint could simply decline to show the secret while
        // still having stored it, so the endpoint is not what is under test here.
        var (hash, prefix) = await ReadStoredAsync(created.Token.Id, ct);
        Assert.DoesNotContain(created.Secret, hash, StringComparison.Ordinal);
        Assert.Equal(PersonalAccessToken.Hash(created.Secret), hash);
        // What is kept in the clear is only enough to tell two tokens apart in a list.
        Assert.Equal(created.Secret[4..12], prefix);
        Assert.Contains(prefix, created.Token.Display, StringComparison.Ordinal);

        // And it is gone from every later read.
        var listed = await ListAsync(ct);
        Assert.DoesNotContain(created.Secret, await ReadListBodyAsync(ct), StringComparison.Ordinal);
        Assert.Equal(created.Token.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task a_token_authenticates_the_person_it_belongs_to()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("laptop CLI", [], ct: ct);

        using var cli = TokenClient(created.Secret);
        var session = await cli.GetFromJsonAsync<SessionResponse>(
            "/api/v1/auth/session", ApiTestContext.Json, ct);

        // The same principal shape as a browser session - that is the point of the policy
        // scheme: one Authorization header, either kind of credential, no endpoint knows.
        Assert.Equal("ada@test.local", session!.Email);
        Assert.Equal(_userId, session.Id);
    }

    [Fact]
    public async Task a_token_that_is_not_ours_is_indistinguishable_from_one_that_never_existed()
    {
        var ct = TestContext.Current.CancellationToken;

        using var invented = TokenClient("aiq_" + new string('a', 40));
        var response = await invented.GetAsync("/api/v1/auth/session", ct);

        // A distinct message would say whether a guessed token had ever existed.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------- scopes

    [Fact]
    public async Task a_read_only_token_can_read_and_cannot_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("ci", [Scopes.Read], ct: ct);

        using var cli = TokenClient(created.Secret);
        var read = await cli.GetAsync($"/api/v1/orgs/{Acme}/members", ct);
        var write = await cli.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("dana@test.local", OrgRole.Member, null, null),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        // 403 and not 404: the caller can see the organization perfectly well - it is
        // their own token that will not let them do this.
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var problem = await write.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/insufficient-scope", problem!.Type);
    }

    [Fact]
    public async Task an_unscoped_token_may_do_whatever_its_owner_may()
    {
        var ct = TestContext.Current.CancellationToken;
        // An empty scope set means *unscoped*, not "permitted nothing" - the same thing a
        // browser session is.
        var created = await CreateAsync("laptop CLI", [], ct: ct);

        using var cli = TokenClient(created.Secret);
        var write = await cli.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("dana@test.local", OrgRole.Member, null, null),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
    }

    [Fact]
    public async Task admin_implies_the_narrower_scopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("deploy", [Scopes.Admin], ct: ct);

        using var cli = TokenClient(created.Secret);
        var read = await cli.GetAsync($"/api/v1/orgs/{Acme}/members", ct);
        var write = await cli.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("dana@test.local", OrgRole.Member, null, null),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
    }

    [Fact]
    public async Task a_narrow_token_cannot_mint_a_wider_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("ci", [Scopes.Read, Scopes.Write], ct: ct);

        using var cli = TokenClient(created.Secret);
        var response = await cli.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("escalation", [Scopes.Admin], null, null),
            ApiTestContext.Json, ct);

        // Otherwise a leaked read-only token would be a leaked everything token.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task an_unknown_scope_is_refused_rather_than_ignored()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _browser.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("typo", ["reed"], null, null), ApiTestContext.Json, ct);

        // Silently dropping it would produce an *unscoped* token from a request that asked
        // for a narrow one.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------------------- binding to an org

    [Fact]
    public async Task a_token_bound_to_an_organization_is_useless_against_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await _browser.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Globex", "globex", null, null), ct);
        other.EnsureSuccessStatusCode();
        var globex = (await other.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;

        var created = await CreateAsync("globex agent", [], globex.Id, ct);
        using var cli = TokenClient(created.Secret);

        var itsOwn = await cli.GetAsync($"/api/v1/orgs/globex/members", ct);
        var theOther = await cli.GetAsync($"/api/v1/orgs/{Acme}/members", ct);

        // Ada is a member of both; the *token* is not. 404, because a bound credential
        // must not even confirm the other organization exists.
        Assert.Equal(HttpStatusCode.OK, itsOwn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theOther.StatusCode);

        // Nor does it list it: "which organizations am I in" answers for the binding.
        var listed = await cli.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);
        Assert.Equal(["globex"], listed!.Select(o => o.Slug).ToArray());
    }

    [Fact]
    public async Task a_token_cannot_be_bound_to_an_organization_you_are_not_in()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _browser.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("elsewhere", [], Guid.NewGuid(), null),
            ApiTestContext.Json, ct);

        // Binding narrows; a narrowing to somewhere you cannot go is nonsense.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ revoking and expiry

    [Fact]
    public async Task a_revoked_token_stops_working_and_says_so_plainly()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("laptop CLI", [], ct: ct);
        using var cli = TokenClient(created.Secret);
        (await cli.GetAsync("/api/v1/auth/session", ct)).EnsureSuccessStatusCode();

        var revoked = await _browser.DeleteAsync($"/api/v1/me/tokens/{created.Token.Id}", ct);
        var after = await cli.GetAsync("/api/v1/auth/session", ct);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // A distinct type, because no amount of retrying or refreshing fixes it and an
        // agent looping on one needs to be told to stop.
        var problem = await after.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/token-revoked", problem!.Type);

        Assert.Empty(await ListAsync(ct));
    }

    [Fact]
    public async Task an_expired_token_is_refused_the_same_way()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("short lived", [], ct: ct);
        await ExpireAsync(created.Token.Id, ct);

        using var cli = TokenClient(created.Secret);
        var response = await cli.GetAsync("/api/v1/auth/session", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/token-revoked", problem!.Type);
    }

    [Fact]
    public async Task revoking_a_token_that_is_not_yours_finds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("laptop CLI", [], ct: ct);

        using var mallory = _context.ClientFor(
            await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney"));
        var response = await mallory.DeleteAsync($"/api/v1/me/tokens/{created.Token.Id}", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task tokens_expire_by_default_rather_than_living_forever()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync("laptop CLI", [], ct: ct);

        // Absent means the default, not "never": a token that never expires is a decision,
        // and it should have to be asked for.
        Assert.NotNull(created.Token.ExpiresAt);
        Assert.True(created.Token.ExpiresAt > DateTimeOffset.UtcNow.AddDays(300));
    }

    // ----------------------------------------------------------------------- last used at

    [Fact]
    public async Task last_used_is_recorded_once_and_then_left_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await CreateAsync("laptop CLI", [], ct: ct);
        using var cli = TokenClient(created.Secret);

        Assert.Null(created.Token.LastUsedAt);

        (await cli.GetAsync("/api/v1/auth/session", ct)).EnsureSuccessStatusCode();
        var first = (await ListAsync(ct)).Single().LastUsedAt;

        for (var i = 0; i < 3; i++)
        {
            (await cli.GetAsync("/api/v1/auth/session", ct)).EnsureSuccessStatusCode();
        }
        var afterMore = (await ListAsync(ct)).Single().LastUsedAt;

        // It answers "is anything still using this", which does not need per-request
        // resolution - and a write on every authenticated request would be one on the
        // hottest path the API has.
        Assert.NotNull(first);
        Assert.Equal(first, afterMore);
    }

    // --------------------------------------------------------------------------- helpers

    private HttpClient TokenClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<AccessTokenCreated> CreateAsync(
        string name, string[] scopes, Guid? organizationId = null, CancellationToken ct = default)
    {
        var response = await _browser.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest(name, scopes, organizationId, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<AccessTokenView>> ListAsync(CancellationToken ct) =>
        (await _browser.GetFromJsonAsync<List<AccessTokenView>>(
            "/api/v1/me/tokens", ApiTestContext.Json, ct))!;

    private async Task<string> ReadListBodyAsync(CancellationToken ct) =>
        await (await _browser.GetAsync("/api/v1/me/tokens", ct)).Content.ReadAsStringAsync(ct);

    private async Task<(string Hash, string Prefix)> ReadStoredAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT token_hash, prefix FROM identity.personal_access_tokens WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>Backdating is the only way to age a token in a test.</summary>
    private async Task ExpireAsync(Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE identity.personal_access_tokens SET expires_at = now() - interval '1 hour' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
}
