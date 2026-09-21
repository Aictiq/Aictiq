using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Agents;

/// <summary>
/// Agent identities: bot members owned by a person, carrying a token instead of a password.
///
/// They are ordinary <c>users</c> rows on purpose — every assignee and author column keeps
/// working — so the tests here are about the three rules that make that safe: an agent
/// cannot log in, it is visibly an agent everywhere, and someone is always answerable for
/// it.
/// </summary>
[Trait("Category", "Agents")]
[Collection("postgres")]
public sealed class AgentTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private const string Acme = "acme";
    private Guid _acmeId;

    private HttpClient _owner = null!;
    private HttpClient _admin = null!;
    private HttpClient _member = null!;
    private string _ownerId = null!;
    private string _adminId = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _context = await ApiTestContext.CreateAsync(postgres, garage, "agents");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _owner = _context.ClientFor(alice);
        _ownerId = alice.User.Id;

        var created = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), ct);
        created.EnsureSuccessStatusCode();
        _acmeId = (await created.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!.Id;

        var bob = await _context.RegisterAsync("bob@test.local", "Bob", "Brooks");
        _admin = _context.ClientFor(bob);
        _adminId = bob.User.Id;
        await AddMemberAsync(bob.User.Id, OrgRole.Admin);

        var carol = await _context.RegisterAsync("carol@test.local", "Carol", "Carter");
        _member = _context.ClientFor(carol);
        await AddMemberAsync(carol.User.Id, OrgRole.Member);
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        _admin.Dispose();
        _member.Dispose();
        await _context.DisposeAsync();
    }

    // -------------------------------------------------------------------------- creating

    [Fact]
    public async Task an_agent_joins_as_a_member_owned_by_whoever_made_it()
    {
        var ct = TestContext.Current.CancellationToken;

        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);

        Assert.Equal("claude-dev", agent.DisplayName);
        Assert.Equal(OrgRole.Member, agent.Role);
        Assert.Equal(_ownerId, agent.OwnerUserId);
        Assert.Equal("Alice Anderson", agent.OwnerName);
        Assert.True(agent.IsActive);

        // The address is synthesised and deliberately undeliverable: an agent has no
        // inbox, and one that looked real would invite someone to write to it.
        Assert.EndsWith($"@agents.{Acme}.invalid", agent.Email, StringComparison.Ordinal);

        // And it is a member like any other, which is the whole reason agents are users.
        var roster = await _owner.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{Acme}/members", ApiTestContext.Json, ct);
        var row = Assert.Single(roster!.Items, m => m.UserId == agent.UserId);
        Assert.True(row.IsAgent);
        Assert.Equal("claude-dev", row.DisplayName);
    }

    [Fact]
    public async Task only_an_admin_can_create_one_and_an_outsider_sees_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var byMember = await PostAsync(_member, new CreateAgentRequest("sneaky", null), ct);
        using var mallory = _context.ClientFor(
            await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney"));
        var byStranger = await PostAsync(mallory, new CreateAgentRequest("sneaky", null), ct);

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
    }

    [Fact]
    public async Task an_agent_can_be_put_straight_into_projects()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreateProjectAsync("Website", "WEB", ct);

        var agent = await CreateAsync(_owner, "release-bot", [project.Id], ct);

        var members = await _owner.GetFromJsonAsync<List<ProjectMemberView>>(
            $"/api/v1/orgs/{Acme}/projects/WEB/members", ApiTestContext.Json, ct);
        var row = Assert.Single(members!, m => m.UserId == agent.UserId && !m.IsImplicit);
        Assert.Equal(ProjectRole.Member, row.Role);
        Assert.True(row.IsAgent);
    }

    [Fact]
    public async Task a_project_that_does_not_exist_fails_before_an_account_is_made()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostAsync(_owner, new CreateAgentRequest("bad-bot", [Guid.NewGuid()]), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Nothing half-created: no agent in the roster to clean up.
        Assert.Empty(await ListAsync(_owner, ct));
    }

    // -------------------------------------------------------------------- cannot log in

    [Fact]
    public async Task an_agent_cannot_log_in_however_hard_anyone_tries()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);

        using var anonymous = _context.Anonymous();
        var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(agent.Email, ApiTestContext.DefaultPassword), ct);

        // It has no password at all — not a random one, because an account with a password
        // nobody knows still has a reset flow, and a reset flow is a way in.
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.False(await HasPasswordAsync(agent.UserId, ct));
    }

    // ---------------------------------------------------------------------- its token

    [Fact]
    public async Task an_agent_works_through_its_token_and_says_it_is_an_agent()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);

        var issued = await IssueTokenAsync(_owner, agent.UserId, "CI", [Scopes.Read, Scopes.Mcp], ct);

        using var bot = TokenClient(issued.Secret);
        var session = await bot.GetFromJsonAsync<SessionResponse>(
            "/api/v1/auth/session", ApiTestContext.Json, ct);

        // whoami, in the words the CLI and MCP actually ask it.
        Assert.True(session!.IsAgent);
        Assert.Equal(agent.UserId, session.Id);

        // And it can read the organization it belongs to.
        Assert.Equal(HttpStatusCode.OK,
            (await bot.GetAsync($"/api/v1/orgs/{Acme}/members", ct)).StatusCode);
    }

    [Fact]
    public async Task an_agents_token_is_bound_to_its_own_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        var globex = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Globex", "globex", null, null), ct);
        globex.EnsureSuccessStatusCode();

        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);
        var issued = await IssueTokenAsync(_owner, agent.UserId, "CI", [], ct);

        using var bot = TokenClient(issued.Secret);
        var elsewhere = await bot.GetAsync("/api/v1/orgs/globex/members", ct);

        // Never unbound: an agent's credential must be useless outside the organization it
        // was made for, and there is no legitimate reading of one that is not.
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public async Task an_owner_can_rotate_their_own_agents_token_without_asking_an_admin()
    {
        var ct = TestContext.Current.CancellationToken;
        // Bob is an admin, so he can create one; the agent is his.
        var agent = await CreateAsync(_admin, "bobs-bot", ct: ct);

        var byOwner = await _admin.PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/agents/{agent.UserId}/tokens",
            new CreateAgentTokenRequest("CI", [], null), ApiTestContext.Json, ct);
        // Alice is an owner of the organization, so she can too.
        var byOrgAdmin = await _owner.PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/agents/{agent.UserId}/tokens",
            new CreateAgentTokenRequest("second", [], null), ApiTestContext.Json, ct);
        // Carol is neither.
        var byStranger = await _member.PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/agents/{agent.UserId}/tokens",
            new CreateAgentTokenRequest("third", [], null), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.Created, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byOrgAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byStranger.StatusCode);
        Assert.Equal(2, (await ListTokensAsync(_admin, agent.UserId, ct)).Count);
    }

    [Fact]
    public async Task an_agent_cannot_mint_its_own_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_owner, "self-service", ct: ct);
        var issued = await IssueTokenAsync(_owner, agent.UserId, "CI", [], ct);
        using var asAgent = TokenClient(issued.Secret);

        // Unscoped, so the scope check passes; the refusal is about *who* is asking. A token
        // minted here would be unbound to the organization and invisible on the agents
        // screen, and revoking the agent would no longer revoke everything it can present.
        var response = await asAgent.PostAsJsonAsync("/api/v1/me/tokens",
            new CreateAccessTokenRequest("successor", [], null, null), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await ListTokensAsync(_owner, agent.UserId, ct));
    }

    // -------------------------------------------------------------------- disabling

    [Fact]
    public async Task disabling_an_agent_kills_its_tokens_without_deleting_its_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);
        var issued = await IssueTokenAsync(_owner, agent.UserId, "CI", [], ct);
        using var bot = TokenClient(issued.Secret);
        (await bot.GetAsync("/api/v1/auth/session", ct)).EnsureSuccessStatusCode();

        var deleted = await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/agents/{agent.UserId}", ct);
        var after = await bot.GetAsync("/api/v1/auth/session", ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // Still there, still disabled: the id is written into everything it ever touched,
        // and removing the row would leave that history pointing at nothing.
        var listed = Assert.Single(await ListAsync(_owner, ct));
        Assert.False(listed.IsActive);
        Assert.Equal(0, listed.TokenCount);
    }

    [Fact]
    public async Task renaming_an_agent_changes_what_it_is_called_everywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_owner, "claude-dev", ct: ct);

        var response = await _owner.PatchAsJsonAsync(
            $"/api/v1/orgs/{Acme}/agents/{agent.UserId}",
            new UpdateAgentRequest("claude-review", null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();

        var roster = await _owner.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{Acme}/members", ApiTestContext.Json, ct);
        Assert.Contains(roster!.Items, m => m.UserId == agent.UserId && m.DisplayName == "claude-review");
    }

    [Fact]
    public async Task an_agent_in_another_organization_cannot_be_reached_from_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var globex = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Globex", "globex", null, null), ct);
        globex.EnsureSuccessStatusCode();

        var elsewhere = await _owner.PostAsJsonAsync("/api/v1/orgs/globex/agents",
            new CreateAgentRequest("globex-bot", null), ApiTestContext.Json, ct);
        elsewhere.EnsureSuccessStatusCode();
        var other = (await elsewhere.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, ct))!;

        var response = await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/agents/{other.UserId}", ct);

        // Alice owns both organizations; the *agent* belongs to one of them.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ListAsync(_owner, ct));
    }

    // ------------------------------------------------------------------ ownership

    [Fact]
    public async Task an_agent_is_handed_to_an_owner_when_the_person_answerable_for_it_leaves()
    {
        var ct = TestContext.Current.CancellationToken;
        // Bob is an admin and makes an agent, so it is his.
        var agent = await CreateAsync(_admin, "bobs-bot", ct: ct);
        Assert.Equal(_adminId, agent.OwnerUserId);

        // He leaves. The handler runs off the outbox in Workers, so the test does what
        // Workers does rather than waiting for a process that is not running.
        (await _admin.DeleteAsync($"/api/v1/orgs/{Acme}/members/{_adminId}", ct)).EnsureSuccessStatusCode();
        await DispatchOwnershipHandlerAsync(_adminId, ct);

        var listed = Assert.Single(await ListAsync(_owner, ct));
        // An agent with no owner keeps acting and nobody is accountable — so it goes to an
        // Owner, which is the one role the organization cannot be left without.
        Assert.Equal(_ownerId, listed.OwnerUserId);
        Assert.Equal("Alice Anderson", listed.OwnerName);
    }

    [Fact]
    public async Task reassigning_ownership_twice_changes_nothing_the_second_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await CreateAsync(_admin, "bobs-bot", ct: ct);
        (await _admin.DeleteAsync($"/api/v1/orgs/{Acme}/members/{_adminId}", ct)).EnsureSuccessStatusCode();

        // Outbox delivery is at least once, so the handler is replayed on purpose.
        await DispatchOwnershipHandlerAsync(_adminId, ct);
        await DispatchOwnershipHandlerAsync(_adminId, ct);

        var listed = Assert.Single(await ListAsync(_owner, ct));
        Assert.Equal(_ownerId, listed.OwnerUserId);
        Assert.Equal(agent.UserId, listed.UserId);
    }

    // --------------------------------------------------------------------------- helpers

    private HttpClient TokenClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private Task<HttpResponseMessage> PostAsync(
        HttpClient client, CreateAgentRequest request, CancellationToken ct) =>
        client.PostAsJsonAsync($"/api/v1/orgs/{Acme}/agents", request, ApiTestContext.Json, ct);

    private async Task<AgentView> CreateAsync(
        HttpClient client, string displayName, IReadOnlyList<Guid>? projectIds = null,
        CancellationToken ct = default)
    {
        var response = await PostAsync(client, new CreateAgentRequest(displayName, projectIds), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<AgentView>> ListAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<AgentView>>(
            $"/api/v1/orgs/{Acme}/agents", ApiTestContext.Json, ct))!;

    private async Task<AgentTokenIssued> IssueTokenAsync(
        HttpClient client, string agentId, string name, string[] scopes, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/orgs/{Acme}/agents/{agentId}/tokens",
            new CreateAgentTokenRequest(name, scopes, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentTokenIssued>(ApiTestContext.Json, ct))!;
    }

    private async Task<List<AgentTokenSummary>> ListTokensAsync(
        HttpClient client, string agentId, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<AgentTokenSummary>>(
            $"/api/v1/orgs/{Acme}/agents/{agentId}/tokens", ApiTestContext.Json, ct))!;

    private async Task<ProjectView> CreateProjectAsync(string name, string key, CancellationToken ct)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest(name, key, null, null, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    /// <summary>
    /// Runs the handler the way Workers would. The API under test does not host the outbox
    /// processor, so the alternative is asserting on a background process that is not
    /// running — and what is under test is the handler's decision, not the outbox, which
    /// has tests of its own.
    /// </summary>
    private async Task DispatchOwnershipHandlerAsync(string departedUserId, CancellationToken ct)
    {
        using var scope = _context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<
            Aictiq.Modules.Tenancy.Events.AgentOwnershipHandler>(scope.ServiceProvider);

        await handler.HandleAsync(
            new Aictiq.Modules.Tenancy.Contracts.OrganizationMembershipChanged(
                _acmeId, departedUserId, null, OrgRole.Admin, _ownerId),
            ct);
    }

    private async Task<bool> HasPasswordAsync(string userId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """SELECT password_hash IS NOT NULL FROM identity."AspNetUsers" WHERE id = @id""", connection);
        command.Parameters.AddWithValue("id", userId);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task AddMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", _acmeId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
