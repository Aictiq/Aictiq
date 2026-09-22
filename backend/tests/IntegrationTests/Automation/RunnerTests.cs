using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Runners: the roster an organization Admin manages, and the <c>jrn_</c> secret a
/// runner speaks the protocol with. Most of what is tested here is the boundary between the
/// two: a runner secret reaches nothing but <c>/runner/*</c>, and nothing but a runner secret
/// reaches <c>/runner/*</c>.
/// </summary>
[Trait("Category", "Automation")]
[Collection("postgres")]
public sealed class RunnerTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    private static readonly RunnerCapabilities Claude = new(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.2.0", 2);

    private ApiTestContext _context = null!;
    private HttpClient _owner = null!;
    private HttpClient _member = null!;
    private HttpClient _stranger = null!;
    private Guid _acmeId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "runners");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _owner = _context.ClientFor(alice);
        var acme = await _owner.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Acme", Acme, null, null), Ct);
        acme.EnsureSuccessStatusCode();
        _acmeId = (await acme.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;

        var carol = await _context.RegisterAsync("carol@test.local", "Carol", "Carter");
        _member = _context.ClientFor(carol);
        await AddMemberAsync(carol.User.Id, OrgRole.Member);

        var dave = await _context.RegisterAsync("dave@test.local", "Dave", "Dawson");
        _stranger = _context.ClientFor(dave);
        (await _stranger.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest("Globex", Globex, null, null), Ct))
            .EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        _member.Dispose();
        _stranger.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task an_admin_registers_a_runner_that_says_hello_and_shows_online()
    {
        var issued = await RegisterAsync(_owner, Acme, "vps-1");
        Assert.StartsWith(RunnerCredential.TokenPrefix, issued.Secret);
        Assert.Equal(RunnerCredential.Display(issued.Secret.Substring(4, 8)), issued.Runner.TokenDisplay);
        Assert.False(issued.Runner.IsOnline);
        Assert.Equal("Alice Anderson", issued.Runner.RegisteredByName);

        // The secret exists once: the roster never carries it, and the database keeps a hash.
        var roster = await _owner.GetStringAsync($"/api/v1/orgs/{Acme}/runners", Ct);
        Assert.DoesNotContain(issued.Secret, roster);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM automation.runners WHERE token_hash = @secret OR token_prefix = @secret", issued.Secret));

        using var runner = RunnerClient(issued.Secret);
        var hello = await runner.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(Claude), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        var greeting = (await hello.Content.ReadFromJsonAsync<RunnerHelloView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(issued.Runner.Id, greeting.RunnerId);
        Assert.Equal(Acme, greeting.OrganizationSlug);
        Assert.Equal("vps-1", greeting.Name);
        Assert.True(greeting.HeartbeatIntervalSeconds > 0);

        var listed = Assert.Single(await ListAsync(_owner, Acme));
        Assert.True(listed.IsOnline);
        Assert.Equal("claude", Assert.Single(listed.Capabilities!.Harnesses).Name);

        // A heartbeat with new capabilities is written even inside the last-seen throttle.
        var codex = Claude with { Harnesses = [new RunnerHarness("claude", "2.1.0"), new RunnerHarness("codex", "0.154.0")] };
        var beat = await runner.PostAsJsonAsync("/api/v1/runner/heartbeat", new RunnerHeartbeatRequest(codex), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);
        Assert.Equal(["claude", "codex"], (await ListAsync(_owner, Acme)).Single().Capabilities!.Harnesses.Select(h => h.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await runner.PostAsync("/api/v1/runner/heartbeat", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task a_runner_secret_reaches_nothing_but_the_runner_protocol()
    {
        var issued = await RegisterAsync(_owner, Acme, "vps-1");
        using var runner = RunnerClient(issued.Secret);

        // Its own organization's routes see a non-member: no user id, no membership.
        Assert.Equal(HttpStatusCode.NotFound, (await runner.GetAsync($"/api/v1/orgs/{Acme}/projects", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await runner.GetAsync($"/api/v1/orgs/{Acme}/runners", Ct)).StatusCode);
        // Routes without an organization see nobody at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.GetAsync("/api/v1/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.GetAsync("/api/v1/orgs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("escape", [Scopes.Admin], null, null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runner.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }), Ct)).StatusCode);
    }

    [Fact]
    public async Task nothing_but_a_runner_secret_reaches_the_runner_protocol()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _owner.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _context.Anonymous().PostAsync("/api/v1/runner/heartbeat", null, Ct)).StatusCode);

        var created = await _owner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("everything", [], _acmeId, null), Ct);
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, Ct))!.Secret;
        using var patClient = RunnerClient(pat);
        Assert.Equal(HttpStatusCode.Unauthorized, (await patClient.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);

        // A secret-shaped guess gets the plain refusal, not "revoked": nothing about it existed.
        using var guess = RunnerClient(RunnerCredential.Generate());
        var refused = await guess.PostAsync("/api/v1/runner/hello", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain("token-revoked", await refused.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task a_disabled_or_deleted_runner_is_told_to_stop()
    {
        var issued = await RegisterAsync(_owner, Acme, "vps-1");
        using var runner = RunnerClient(issued.Secret);
        Assert.Equal(HttpStatusCode.OK, (await runner.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);

        // The hello above rewrote the row; an Admin's edit carries no version and does not
        // lose that race to a machine.
        var disabled = await PatchAsync(issued.Runner.Id, new UpdateRunnerRequest(null, true));
        Assert.True(disabled.IsDisabled);
        Assert.False(disabled.IsOnline);
        await AssertRevokedAsync(runner);

        var enabled = await PatchAsync(issued.Runner.Id, new UpdateRunnerRequest("vps-one", false));
        Assert.False(enabled.IsDisabled);
        Assert.Equal("vps-one", enabled.Name);
        Assert.Equal(HttpStatusCode.OK, (await runner.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/runners/{issued.Runner.Id}", Ct)).StatusCode);
        await AssertRevokedAsync(runner);
        Assert.Empty(await ListAsync(_owner, Acme));
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/runners/{issued.Runner.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.PostAsync($"/api/v1/orgs/{Acme}/runners/{issued.Runner.Id}/rotate", null, Ct)).StatusCode);

        // The row stays (runs will name it); the name is free again.
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM automation.runners WHERE id::text = @secret AND deleted_at IS NOT NULL", issued.Runner.Id.ToString()));
        await RegisterAsync(_owner, Acme, "VPS-ONE");
    }

    [Fact]
    public async Task rotating_a_secret_kills_the_old_one()
    {
        var issued = await RegisterAsync(_owner, Acme, "vps-1");
        var rotated = await _owner.PostAsync($"/api/v1/orgs/{Acme}/runners/{issued.Runner.Id}/rotate", null, Ct);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var fresh = (await rotated.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, Ct))!;
        Assert.NotEqual(issued.Secret, fresh.Secret);

        using var old = RunnerClient(issued.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);
        using var current = RunnerClient(fresh.Secret);
        Assert.Equal(HttpStatusCode.OK, (await current.PostAsync("/api/v1/runner/hello", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task the_roster_is_an_admins_and_invisible_to_everyone_else()
    {
        await RegisterAsync(_owner, Acme, "vps-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await _member.GetAsync($"/api/v1/orgs/{Acme}/runners", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _member.PostAsJsonAsync($"/api/v1/orgs/{Acme}/runners", new CreateRunnerRequest("mine"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _stranger.GetAsync($"/api/v1/orgs/{Acme}/runners", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _stranger.PostAsJsonAsync($"/api/v1/orgs/{Acme}/runners", new CreateRunnerRequest("mine"), Ct)).StatusCode);

        // A runner id from another organization is not found through this one.
        var theirs = await RegisterAsync(_stranger, Globex, "globex-box");
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.PostAsync($"/api/v1/orgs/{Acme}/runners/{theirs.Runner.Id}/rotate", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/runners/{theirs.Runner.Id}", Ct)).StatusCode);

        // Registering mints a credential, so a token without the admin scope cannot.
        var created = await _owner.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("rw", [Scopes.Read, Scopes.Write], _acmeId, null), Ct);
        created.EnsureSuccessStatusCode();
        using var readWrite = RunnerClient((await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, Ct))!.Secret);
        Assert.Equal(HttpStatusCode.OK, (await readWrite.GetAsync($"/api/v1/orgs/{Acme}/runners", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readWrite.PostAsJsonAsync($"/api/v1/orgs/{Acme}/runners", new CreateRunnerRequest("sneaky"), Ct)).StatusCode);
    }

    [Fact]
    public async Task a_runner_knows_only_its_own_organization()
    {
        var ours = await RegisterAsync(_owner, Acme, "shared-name");
        var theirs = await RegisterAsync(_stranger, Globex, "shared-name");

        using var runner = RunnerClient(theirs.Secret);
        var hello = await runner.PostAsync("/api/v1/runner/hello", null, Ct);
        Assert.Equal(Globex, (await hello.Content.ReadFromJsonAsync<RunnerHelloView>(ApiTestContext.Json, Ct))!.OrganizationSlug);
        Assert.Equal(HttpStatusCode.NotFound, (await runner.GetAsync($"/api/v1/orgs/{Acme}/runners", Ct)).StatusCode);

        // Its hello marked itself, not the namesake in another organization.
        Assert.Null(Assert.Single(await ListAsync(_owner, Acme)).LastSeenAt);
        Assert.Equal(ours.Runner.Id, Assert.Single(await ListAsync(_owner, Acme)).Id);
    }

    [Fact]
    public async Task names_are_unique_per_organization_ignoring_case_and_capabilities_are_validated()
    {
        await RegisterAsync(_owner, Acme, "vps-1");
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/runners", new CreateRunnerRequest("VPS-1"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/runners", new CreateRunnerRequest("  "), Ct)).StatusCode);

        var issued = await RegisterAsync(_owner, Acme, "vps-2");
        using var runner = RunnerClient(issued.Secret);
        foreach (var bad in new[]
        {
            Claude with { V = 2 },
            Claude with { Harnesses = [new RunnerHarness("Claude Code", null)] },
            Claude with { MaxParallel = 0 },
        })
        {
            var refused = await runner.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(bad), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task the_database_refuses_a_deleted_runner_that_could_still_authenticate()
    {
        var issued = await RegisterAsync(_owner, Acme, "vps-1");
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync("UPDATE automation.runners SET deleted_at = now(), disabled_at = NULL WHERE id::text = @secret RETURNING 1", issued.Runner.Id.ToString()));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private HttpClient RunnerClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<RunnerIssuedView> RegisterAsync(HttpClient client, string slug, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{slug}/runners", new CreateRunnerRequest(name), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<List<RunnerView>> ListAsync(HttpClient client, string slug) =>
        (await client.GetFromJsonAsync<List<RunnerView>>($"/api/v1/orgs/{slug}/runners", ApiTestContext.Json, Ct))!;

    private async Task<RunnerView> PatchAsync(Guid runnerId, UpdateRunnerRequest request)
    {
        var response = await _owner.PatchAsJsonAsync($"/api/v1/orgs/{Acme}/runners/{runnerId}", request, ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunnerView>(ApiTestContext.Json, Ct))!;
    }

    private async Task AssertRevokedAsync(HttpClient runner)
    {
        var response = await runner.PostAsync("/api/v1/runner/heartbeat", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemTypes.TokenRevoked, problem.RootElement.GetProperty("type").GetString());
    }

    private async Task AddMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, @role, now())",
            connection);
        command.Parameters.AddWithValue("org", _acmeId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql, string secret)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("secret", secret);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct) ?? 0L);
    }
}
