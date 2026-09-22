using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// The first-login product tour's per-account preference: one row per person,
/// written only through compare-and-swap, refused to agents, seeded dismissed for
/// everyone who already had an account when the rollout migration ran.
///
/// A test database cannot contain people who predate its migrations, so the rollout is
/// covered by running the migration's own INSERT against the live database
/// (<see cref="the_rollout_backfill_dismisses_existing_humans_skips_agents_and_is_idempotent"/>)
/// and by proving new accounts stay not_started. The check constraints are exercised with
/// raw SQL on purpose: the endpoint validating nicely is not the guarantee.
/// </summary>
[Trait("Category", "Auth")]
[Collection("postgres")]
public sealed class OnboardingTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private string _userId = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "onboarding");
        var auth = await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");
        _client = _context.ClientFor(auth);
        _userId = auth.User.Id;
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    // ------------------------------------------------------------ reading and writing

    [Fact]
    public async Task a_new_account_reads_as_not_started_and_get_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var view = await GetAsync(ct);

        Assert.Equal(1, view.TourVersion);
        Assert.Equal("not_started", view.Status);
        Assert.Null(view.LastStepId);
        Assert.Null(view.CompletedAt);
        Assert.Equal(0u, view.Version);
        // The row comes into existence on the first write, not because somebody asked.
        Assert.Equal(0, await RowCountAsync(ct));
    }

    [Fact]
    public async Task starting_the_tour_writes_in_progress_and_moves_the_version()
    {
        var ct = TestContext.Current.CancellationToken;

        var started = await PatchAsync(new UpdateOnboardingRequest("in_progress", "navigation", 0), ct);

        Assert.Equal("in_progress", started.Status);
        Assert.Equal("navigation", started.LastStepId);
        Assert.NotEqual(0u, started.Version);
        Assert.Null(started.CompletedAt);

        var reread = await GetAsync(ct);
        Assert.Equal(started.Version, reread.Version);
    }

    [Fact]
    public async Task a_stale_version_is_a_conflict_and_the_winner_stands()
    {
        var ct = TestContext.Current.CancellationToken;

        var started = await PatchAsync(new UpdateOnboardingRequest("in_progress", "board", 0), ct);

        var stale = await _client.PatchAsJsonAsync("/api/v1/me/onboarding",
            new UpdateOnboardingRequest("dismissed", null, 0), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var view = await GetAsync(ct);
        Assert.Equal("in_progress", view.Status);
        Assert.Equal(started.Version, view.Version);
    }

    [Fact]
    public async Task two_racing_first_writes_produce_one_row_and_one_conflict()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two sessions of the same person, both convinced there is no row yet. The
        // compare-and-swap (and behind it the primary key), not an if, picks the winner.
        var second = await _context.LoginAsync("ada@test.local", ApiTestContext.DefaultPassword);
        using var other = _context.ClientFor(second);

        var racers = await Task.WhenAll(
            _client.PatchAsJsonAsync("/api/v1/me/onboarding",
                new UpdateOnboardingRequest("in_progress", "navigation", 0), ApiTestContext.Json, ct),
            other.PatchAsJsonAsync("/api/v1/me/onboarding",
                new UpdateOnboardingRequest("in_progress", "organization", 0), ApiTestContext.Json, ct));

        Assert.Equal(1, racers.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, racers.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await RowCountAsync(ct));
    }

    [Fact]
    public async Task later_defers_and_keeps_the_step_for_the_resume_entry()
    {
        var ct = TestContext.Current.CancellationToken;

        var started = await PatchAsync(new UpdateOnboardingRequest("in_progress", "prepare-item", 0), ct);
        var deferred = await PatchAsync(new UpdateOnboardingRequest("deferred", "prepare-item", started.Version), ct);

        Assert.Equal("deferred", deferred.Status);
        Assert.Equal("prepare-item", deferred.LastStepId);
        Assert.Null(deferred.CompletedAt);
    }

    [Fact]
    public async Task skipping_dismisses_and_forgets_the_step()
    {
        var ct = TestContext.Current.CancellationToken;

        var started = await PatchAsync(new UpdateOnboardingRequest("in_progress", "board", 0), ct);
        var skipped = await PatchAsync(new UpdateOnboardingRequest("dismissed", null, started.Version), ct);

        Assert.Equal("dismissed", skipped.Status);
        Assert.Null(skipped.LastStepId);
    }

    [Fact]
    public async Task finishing_stamps_completion_once_and_repeats_do_not_move_it()
    {
        var ct = TestContext.Current.CancellationToken;

        var finished = await PatchAsync(new UpdateOnboardingRequest("completed", null, 0), ct);
        Assert.Equal("completed", finished.Status);
        Assert.NotNull(finished.CompletedAt);

        // Two tabs finishing, or a client replaying its last write with the fresh version,
        // must not move the date the tour actually ended.
        var again = await PatchAsync(new UpdateOnboardingRequest("completed", null, finished.Version), ct);
        Assert.Equal(finished.CompletedAt, again.CompletedAt);
    }

    [Fact]
    public async Task an_unknown_step_or_status_is_refused_before_anything_is_written()
    {
        var ct = TestContext.Current.CancellationToken;

        var unknownStep = await _client.PatchAsJsonAsync("/api/v1/me/onboarding",
            new UpdateOnboardingRequest("in_progress", "secret-basement", 0), ApiTestContext.Json, ct);
        var unknownStatus = await _client.PatchAsJsonAsync("/api/v1/me/onboarding",
            new UpdateOnboardingRequest("sort_of_done", null, 0), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, unknownStep.StatusCode);
        Assert.Contains("lastStepId", await unknownStep.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, unknownStatus.StatusCode);
        Assert.Equal(0, await RowCountAsync(ct));
    }

    [Fact]
    public async Task onboarding_state_does_not_leak_between_accounts()
    {
        var ct = TestContext.Current.CancellationToken;

        _ = await PatchAsync(new UpdateOnboardingRequest("completed", null, 0), ct);

        var grace = await _context.RegisterAsync("grace@test.local", "Grace", "Hopper");
        using var hers = _context.ClientFor(grace);

        Assert.Equal("completed", (await GetAsync(ct)).Status);
        Assert.Equal("not_started", (await GetAsync(hers, ct)).Status);
    }

    // ------------------------------------------------------------ who may write

    [Fact]
    public async Task an_agent_reads_but_can_never_write_onboarding()
    {
        var ct = TestContext.Current.CancellationToken;

        var issued = await IssueAgentTokenAsync(ct);
        using var asAgent = TokenClient(issued.Secret);

        var read = await asAgent.GetAsync("/api/v1/me/onboarding", ct);
        var write = await asAgent.PatchAsJsonAsync("/api/v1/me/onboarding",
            new UpdateOnboardingRequest("completed", null, 0), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var problem = await write.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal(ProblemTypes.InsufficientRole, problem!.Type);
        // The refusal left no preference row behind for a principal that cannot use one.
        Assert.Equal(0, await RowCountAsync(ct, issued.UserId));
    }

    [Fact]
    public async Task a_read_only_token_can_read_but_not_write()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await _client.PostAsJsonAsync("/api/v1/me/tokens",
            new { name = "ci", scopes = new[] { Scopes.Read } }, ApiTestContext.Json, ct);
        created.EnsureSuccessStatusCode();
        var issued = await created.Content.ReadFromJsonAsync<CreatedToken>(ApiTestContext.Json, ct);
        using var cli = TokenClient(issued!.Secret);

        var read = await cli.GetAsync("/api/v1/me/onboarding", ct);
        var write = await cli.PatchAsJsonAsync("/api/v1/me/onboarding",
            new UpdateOnboardingRequest("completed", null, 0), ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var problem = await write.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal(ProblemTypes.InsufficientScope, problem!.Type);
    }

    // ------------------------------------------------------------ what the database enforces

    [Fact]
    public async Task the_database_refuses_an_unknown_status_and_an_unpaired_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);

        await using (var unknown = new NpgsqlCommand(
            """
            INSERT INTO identity.user_onboarding (user_id, tour_version, status, updated_at)
            VALUES (@userId, 1, 'wip', now())
            """, connection))
        {
            unknown.Parameters.AddWithValue("userId", _userId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => unknown.ExecuteNonQueryAsync(ct));
            Assert.Contains("ck_user_onboarding_status", ex.Message, StringComparison.Ordinal);
        }

        await using (var unpaired = new NpgsqlCommand(
            """
            INSERT INTO identity.user_onboarding (user_id, tour_version, status, updated_at)
            VALUES (@userId, 1, 'completed', now())
            """, connection))
        {
            unpaired.Parameters.AddWithValue("userId", _userId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => unpaired.ExecuteNonQueryAsync(ct));
            Assert.Contains("ck_user_onboarding_completed_at", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task a_person_has_exactly_one_onboarding_row()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = await PatchAsync(new UpdateOnboardingRequest("in_progress", "board", 0), ct);

        await using var connection = await OpenAsync(ct);
        await using var duplicate = new NpgsqlCommand(
            """
            INSERT INTO identity.user_onboarding (user_id, tour_version, status, updated_at)
            VALUES (@userId, 1, 'dismissed', now())
            """, connection);
        duplicate.Parameters.AddWithValue("userId", _userId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync(ct));
        Assert.Contains("pk_user_onboarding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_row_written_directly_reads_back_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = await OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO identity.user_onboarding (user_id, tour_version, status, completed_at, updated_at)
            VALUES (@userId, 1, 'completed', now() - interval '3 days', now())
            """, connection);
        insert.Parameters.AddWithValue("userId", _userId);
        await insert.ExecuteNonQueryAsync(ct);

        var view = await GetAsync(ct);
        Assert.Equal("completed", view.Status);
        Assert.NotNull(view.CompletedAt);
        Assert.NotEqual(0u, view.Version);
    }

    [Fact]
    public async Task the_rollout_backfill_dismisses_existing_humans_skips_agents_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Someone who was here before release day, and the agent their organization owned.
        var agent = await CreateAgentAsync(ct);
        var grace = await _context.RegisterAsync("grace@test.local", "Grace", "Hopper");

        await RunBackfillStatementAsync(ct);

        Assert.Equal("dismissed", (await GetAsync(ct)).Status);
        Assert.Equal("dismissed", (await GetAsync(_context.ClientFor(grace), ct)).Status);
        Assert.Equal(0, await RowCountAsync(ct, agent.UserId));

        // One row per human the instance has - Ada, Grace and the seeded owner - and
        // none for the agent. Counting the users rather than hard-coding a number keeps
        // the guarantee ("every human, no agent") independent of what the fixture seeds.
        var backfilled = await RowCountAsync(ct);
        Assert.Equal(await HumanCountAsync(ct), backfilled);

        // Release day happens once; running it again must neither duplicate nor change.
        await RunBackfillStatementAsync(ct);
        Assert.Equal(backfilled, await RowCountAsync(ct));
    }

    // ------------------------------------------------------------ helpers

    private Task<OnboardingView> GetAsync(CancellationToken ct) => GetAsync(_client, ct);

    private static async Task<OnboardingView> GetAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<OnboardingView>("/api/v1/me/onboarding", ApiTestContext.Json, ct))!;

    private async Task<OnboardingView> PatchAsync(UpdateOnboardingRequest request, CancellationToken ct)
    {
        var response = await _client.PatchAsJsonAsync("/api/v1/me/onboarding", request, ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OnboardingView>(ApiTestContext.Json, ct))!;
    }

    private HttpClient TokenClient(string secret)
    {
        var client = _context.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<AgentView> CreateAgentAsync(CancellationToken ct)
    {
        var created = await _client.PostAsJsonAsync("/api/v1/orgs",
            new { name = "Acme", slug = "acme" }, ApiTestContext.Json, ct);
        created.EnsureSuccessStatusCode();

        var agent = await _client.PostAsJsonAsync("/api/v1/orgs/acme/agents",
            new { displayName = "Bot" }, ApiTestContext.Json, ct);
        agent.EnsureSuccessStatusCode();
        return (await agent.Content.ReadFromJsonAsync<AgentView>(ApiTestContext.Json, ct))!;
    }

    private async Task<(string Secret, string UserId)> IssueAgentTokenAsync(CancellationToken ct)
    {
        var agent = await CreateAgentAsync(ct);
        var token = await _client.PostAsJsonAsync($"/api/v1/orgs/acme/agents/{agent.UserId}/tokens",
            new { name = "tour" }, ApiTestContext.Json, ct);
        token.EnsureSuccessStatusCode();
        var issued = await token.Content.ReadFromJsonAsync<AgentTokenIssued>(ApiTestContext.Json, ct);
        return (issued!.Secret, agent.UserId);
    }

    /// <summary>
    /// The migration's own INSERT, run by hand. Carried as a copy because the rollout's
    /// semantics - humans become dismissed, agents get nothing, running it twice changes
    /// nothing - are what the guarantee is made of.
    /// </summary>
    private async Task RunBackfillStatementAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var backfill = new NpgsqlCommand(
            """
            INSERT INTO identity.user_onboarding (user_id, tour_version, status, updated_at)
            SELECT id, 1, 'dismissed', now()
            FROM identity."AspNetUsers"
            WHERE is_agent = FALSE
            ON CONFLICT (user_id) DO NOTHING;
            """, connection);
        await backfill.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>People with an account - agents excluded, as the backfill excludes them.</summary>
    private async Task<int> HumanCountAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM identity.\"AspNetUsers\" WHERE is_agent = FALSE", connection);
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Rows for one person, or across the whole table when no id is given.</summary>
    private async Task<int> RowCountAsync(CancellationToken ct, string? userId = null)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM identity.user_onboarding"
            + (userId is null ? "" : " WHERE user_id = @userId"),
            connection);
        if (userId is not null)
        {
            command.Parameters.AddWithValue("userId", userId);
        }
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    private sealed record AgentView(string UserId);

    private sealed record AgentTokenIssued(string Secret);

    private sealed record CreatedToken(TokenView Token, string Secret);

    private sealed record TokenView(string Id);
}
