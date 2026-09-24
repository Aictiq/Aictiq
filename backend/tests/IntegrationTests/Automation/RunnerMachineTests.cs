using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// One machine serving several organizations: the Admin's own runners elsewhere, grouped by the
/// machine id the CLI reports, and registering that machine here as a new runner. The machine
/// id only groups; every registration keeps its own secret and its own organization.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RunnerMachineTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Acme = "acme";
    private const string Initech = "initech";
    private const string Hooli = "hooli";
    private const string Globex = "globex";

    private static readonly string MachineId = Guid.NewGuid().ToString();
    private static readonly RunnerCapabilities Laptop =
        new(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.3.0", 1, MachineId);

    private ApiTestContext _context = null!;
    private HttpClient _alice = null!;
    private HttpClient _carol = null!;
    private HttpClient _dave = null!;
    private string _aliceId = null!;
    private readonly Dictionary<string, Guid> _organizations = [];
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "runner_machines");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _aliceId = alice.User.Id;
        _alice = _context.ClientFor(alice);
        foreach (var (name, slug) in new[] { ("Acme", Acme), ("Initech", Initech), ("Hooli", Hooli) })
        {
            await CreateOrganizationAsync(_alice, name, slug);
        }

        var carol = await _context.RegisterAsync("carol@test.local", "Carol", "Carter");
        _carol = _context.ClientFor(carol);
        await AddMemberAsync(Acme, carol.User.Id, OrgRole.Admin);

        var dave = await _context.RegisterAsync("dave@test.local", "Dave", "Dawson");
        _dave = _context.ClientFor(dave);
        await CreateOrganizationAsync(_dave, "Globex", Globex);
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _carol.Dispose();
        _dave.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task a_machine_registered_elsewhere_is_listed_once_and_registered_here_as_a_new_runner()
    {
        var laptop = await RegisterAsync(_alice, Acme, new CreateRunnerRequest("laptop"));
        using (var runner = RunnerClient(laptop.Secret))
        {
            var hello = await runner.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(Laptop), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        }

        var offered = Assert.Single(await ElsewhereAsync(_alice, Initech));
        Assert.Equal(laptop.Runner.Id, offered.RunnerId);
        Assert.Equal("laptop", offered.Name);
        Assert.True(offered.IsOnline);
        Assert.False(offered.IsConnectedHere);
        Assert.Equal(Acme, Assert.Single(offered.Organizations).Slug);

        // Registered here, the machine is a new runner with its own secret; its name and
        // last report come along so it groups with its other registrations straight away.
        var here = await RegisterAsync(_alice, Initech, new CreateRunnerRequest(null, laptop.Runner.Id));
        Assert.Equal("laptop", here.Runner.Name);
        Assert.NotEqual(laptop.Secret, here.Secret);
        Assert.Equal(MachineId, here.Runner.Capabilities?.MachineId);
        Assert.Null(here.Runner.LastSeenAt);
        Assert.True(Assert.Single(await ElsewhereAsync(_alice, Initech)).IsConnectedHere);

        // Each secret still reaches only its own organization.
        using (var runner = RunnerClient(here.Secret))
        {
            var hello = await runner.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(Laptop), ApiTestContext.Json, Ct);
            Assert.Equal(Initech, (await hello.Content.ReadFromJsonAsync<RunnerHelloView>(ApiTestContext.Json, Ct))!.OrganizationSlug);
        }

        // A third organization sees one machine with two registrations, and a runner that
        // never reported a machine id as a machine of its own.
        var vps = await RegisterAsync(_alice, Acme, new CreateRunnerRequest("vps"));
        var machines = await ElsewhereAsync(_alice, Hooli);
        Assert.Equal(["laptop", "vps"], machines.Select(m => m.Name));
        Assert.Equal([Acme, Initech], machines[0].Organizations.Select(o => o.Slug));

        // A disabled runner is not offered.
        (await _alice.PatchAsJsonAsync($"/api/v1/orgs/{Acme}/runners/{vps.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("laptop", Assert.Single(await ElsewhereAsync(_alice, Hooli)).Name);

        // The name can be overridden, and a taken name is the usual conflict.
        var renamed = await RegisterAsync(_alice, Hooli, new CreateRunnerRequest("laptop-hooli", laptop.Runner.Id));
        Assert.Equal("laptop-hooli", renamed.Runner.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await _alice.PostAsJsonAsync($"/api/v1/orgs/{Initech}/runners",
            new CreateRunnerRequest(null, laptop.Runner.Id), ApiTestContext.Json, Ct)).StatusCode);
    }

    [Fact]
    public async Task only_the_callers_own_runners_in_organizations_they_administer_are_offered()
    {
        // Carol is an Admin of Acme too, but her runner is hers.
        var carols = await RegisterAsync(_carol, Acme, new CreateRunnerRequest("carol-box"));
        // Alice registered a runner in Globex as an Admin, then became a Member there.
        await AddMemberAsync(Globex, _aliceId, OrgRole.Admin);
        var demoted = await RegisterAsync(_alice, Globex, new CreateRunnerRequest("globex-box"));
        await ExecuteAsync(Globex, "UPDATE tenancy.organization_members SET role = @role WHERE organization_id = @org AND user_id = @user",
            ("role", (short)OrgRole.Member), ("user", _aliceId));
        // Dave's runner lives in the one organization he and Alice share.
        var daves = await RegisterAsync(_dave, Globex, new CreateRunnerRequest("dave-box"));

        Assert.Empty(await ElsewhereAsync(_alice, Initech));
        foreach (var foreign in new[] { carols.Runner.Id, demoted.Runner.Id, daves.Runner.Id })
        {
            var refused = await _alice.PostAsJsonAsync($"/api/v1/orgs/{Initech}/runners",
                new CreateRunnerRequest(null, foreign), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // A runner of this organization is not "elsewhere".
        var local = await RegisterAsync(_alice, Initech, new CreateRunnerRequest("initech-box"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _alice.PostAsJsonAsync($"/api/v1/orgs/{Initech}/runners",
            new CreateRunnerRequest("copy", local.Runner.Id), ApiTestContext.Json, Ct)).StatusCode);

        // Carol sees her own runner from nowhere else, since Acme is her only organization.
        Assert.Equal(HttpStatusCode.NotFound, (await _carol.GetAsync($"/api/v1/orgs/{Initech}/runners/elsewhere", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _dave.GetAsync($"/api/v1/orgs/{Initech}/runners/elsewhere", Ct)).StatusCode);
    }

    [Fact]
    public async Task a_token_bound_to_one_organization_learns_nothing_about_the_others()
    {
        await RegisterAsync(_alice, Acme, new CreateRunnerRequest("laptop"));
        Assert.Single(await ElsewhereAsync(_alice, Initech));

        var created = await _alice.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("initech-admin", [Scopes.Read, Scopes.Admin], _organizations[Initech], null), Ct);
        created.EnsureSuccessStatusCode();
        using var bound = TokenClient((await created.Content.ReadFromJsonAsync<AccessTokenCreated>(ApiTestContext.Json, Ct))!.Secret);
        Assert.Empty((await bound.GetFromJsonAsync<List<RunnerMachineView>>($"/api/v1/orgs/{Initech}/runners/elsewhere", ApiTestContext.Json, Ct))!);
    }

    [Fact]
    public async Task a_member_cannot_list_or_copy_runners()
    {
        var eve = await _context.RegisterAsync("eve@test.local", "Eve", "Evans");
        using var member = _context.ClientFor(eve);
        await AddMemberAsync(Initech, eve.User.Id, OrgRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v1/orgs/{Initech}/runners/elsewhere", Ct)).StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private async Task CreateOrganizationAsync(HttpClient client, string name, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest(name, slug, null, null), Ct);
        response.EnsureSuccessStatusCode();
        _organizations[slug] = (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;
    }

    private async Task<RunnerIssuedView> RegisterAsync(HttpClient client, string slug, CreateRunnerRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{slug}/runners", request, ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunnerIssuedView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<List<RunnerMachineView>> ElsewhereAsync(HttpClient client, string slug) =>
        (await client.GetFromJsonAsync<List<RunnerMachineView>>($"/api/v1/orgs/{slug}/runners/elsewhere", ApiTestContext.Json, Ct))!;

    private HttpClient RunnerClient(string secret) => TokenClient(secret);

    private HttpClient TokenClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private Task AddMemberAsync(string slug, string userId, OrgRole role) =>
        ExecuteAsync(slug, "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at) VALUES (@org, @user, @role, now())",
            ("user", userId), ("role", (short)role));

    private async Task ExecuteAsync(string slug, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", _organizations[slug]);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
