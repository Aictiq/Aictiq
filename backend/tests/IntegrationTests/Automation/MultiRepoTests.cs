using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Workers;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Multiple repositories and monorepo sub-packages on a hosted instance: only a paid organization
/// may turn them on; refine, chat, project-chat and implement runs check out every repository,
/// preferably on a runner that has them all, else on one that has some and says which it lacks; a
/// plain <c>repo:</c> label is a hint, while ordering labels and a playbook's graph run a later
/// repository after an earlier one's pull request merges; and a project that downgrades falls
/// back to its primary repository.
/// </summary>
[Trait("Category", "Automation")]
public sealed class MultiRepoTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Billing:Mode"] = "saas";
        settings["Automation:PollTimeoutSeconds"] = "1";
    };

    private string SettingsUrl => $"{ProjectBase}/factory-settings";

    private async Task<FactorySettingsView> SettingsAsync() =>
        (await Owner.GetFromJsonAsync<FactorySettingsView>(SettingsUrl, ApiTestContext.Json, Ct))!;

    private async Task<HttpResponseMessage> PutAsync(
        bool? enabled = null, IReadOnlyList<ProjectRepositoryView>? additional = null, string? primaryWorkingDirectory = null)
    {
        var current = await SettingsAsync();
        return await Owner.PutAsJsonAsync(SettingsUrl,
            new UpdateFactorySettingsRequest(current.RepoSource, current.RepoFullName, current.DefaultBranch,
                current.LocalPathHint, current.DefaultAgentId, current.Version, current.DefaultRunnerId,
                enabled, null, primaryWorkingDirectory, additional),
            ApiTestContext.Json, Ct);
    }

    private Task SeedPaidSubscriptionAsync() => ExecuteSqlAsync($"""
        INSERT INTO billing.subscriptions (id, organization_id, plan, stripe_customer_id, stripe_subscription_id, status,
            cancel_at_period_end, seats_human, seats_agent, created_at, updated_at)
        VALUES (gen_random_uuid(), '{OrganizationId}', 'hosted', 'cus_multi', 'sub_multi', 4, false, 1, 0, now(), now())
        """);

    /// <summary>A paid organization whose project has the primary <c>acme</c> (/srv/acme) and a local <c>api</c>.</summary>
    private async Task EnableAsync()
    {
        await SeedPaidSubscriptionAsync();
        var saved = await PutAsync(true, [new ProjectRepositoryView("api", 1, null, "/srv/api", "develop", "packages/api")]);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
    }

    private async Task HelloAsync(string secret, bool multiRepo = true, IReadOnlyList<string>? workspaces = null)
    {
        using var client = RunnerClient(secret);
        var capabilities = new RunnerCapabilities(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.18.0",
            1, Workspaces: workspaces ?? ["WEB", "WEB/api"], MultiRepo: multiRepo ? true : null);
        var response = await client.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task LabelAsync(string itemKey, string name)
    {
        var created = await Owner.PostAsJsonAsync($"{ProjectBase}/labels", new CreateLabelRequest(name, null, null, null), ApiTestContext.Json, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var label = (await created.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!;
        (await Owner.PutAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/labels/{label.Id}", null, Ct)).EnsureSuccessStatusCode();
    }

    private async Task SetStepsAsync(params RepositoryStep[] steps)
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        var saved = await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
            new UpdatePlaybookRequest { RepositorySteps = steps, Version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
    }

    private async Task SettleAsync(Guid runId)
    {
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(runId));
        await ExecuteSqlAsync($"""
            UPDATE shared.outbox_messages SET processed_at = now()
            WHERE type = '{typeof(RunFinished).FullName}' AND payload::jsonb ->> 'RunId' = '{runId}'
            """);
    }

    private async Task SweepAsync() =>
        await ActivatorUtilities.CreateInstance<RunSweeper>(Context.Factory.Services).RunOnceAsync(Ct);

    private async Task<CommentView> CommentAsync(string itemKey, string body)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/comments/",
            new CreateCommentRequest(body, null), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, Ct))!;
    }

    /// <summary>Mentions the agent on the item and delivers the staged <c>CommentAdded</c>, as the outbox would: a chat run.</summary>
    private async Task<RunView> ChatAsync(string itemKey, string question)
    {
        var comment = await CommentAsync(itemKey, $"@builder {question}");
        await using (var connection = new NpgsqlConnection(Context.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT payload FROM shared.outbox_messages WHERE type = @type AND payload::jsonb ->> 'CommentId' = @id", connection);
            command.Parameters.AddWithValue("type", typeof(CommentAdded).FullName!);
            command.Parameters.AddWithValue("id", comment.Id.ToString());
            var @event = JsonSerializer.Deserialize<CommentAdded>(Assert.IsType<string>(await command.ExecuteScalarAsync(Ct)))!;
            using var scope = Context.Factory.Services.CreateScope();
            await ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Events.AgentMentionHandler>(scope.ServiceProvider)
                .HandleAsync(@event, Ct);
        }
        var runs = (await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{itemKey}/runs", ApiTestContext.Json, Ct))!.Items;
        return await RunAsync(Assert.Single(runs, run => run.TriggerCommentId == comment.Id).Id);
    }

    /// <summary>Turns refinement on with its starter playbook and refines the item: a refine run.</summary>
    private async Task<RunView> RefineAsync(string itemKey)
    {
        var starter = await Owner.PostAsync($"{ProjectBase}/refinement-settings/starter-playbook", null, Ct);
        Assert.True(starter.IsSuccessStatusCode || starter.StatusCode == HttpStatusCode.Conflict, await starter.Content.ReadAsStringAsync(Ct));
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/refinement/", new RefineItemRequest(), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var refinement = (await response.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
        return await RunAsync(refinement.LastRunId!.Value);
    }

    /// <summary>Asks a question in the owner's project chat: a project-chat run.</summary>
    private async Task<RunView> ProjectChatAsync(string question)
    {
        var chat = await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question }, ApiTestContext.Json, Ct);
        Assert.True(chat.StatusCode == HttpStatusCode.Created, await chat.Content.ReadAsStringAsync(Ct));
        var runId = await ScalarGuidAsync("SELECT id FROM automation.runs WHERE kind = 4 ORDER BY queued_at DESC LIMIT 1");
        return await RunAsync(runId);
    }

    private async Task<Guid> ScalarGuidAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsType<Guid>(await command.ExecuteScalarAsync(Ct));
    }

    private Task<HttpResponseMessage> FinishWithAsync(HttpClient runner, Guid runId, params string[] pullRequests) =>
        runner.PostAsJsonAsync($"/api/v1/runner/runs/{runId}/finish",
            new RunnerFinishRequest(RunOutcomes.Succeeded, 0, "Done.", pullRequests.FirstOrDefault(), null, null, null, null,
                PullRequestUrls: pullRequests),
            ApiTestContext.Json, Ct);

    [Fact]
    public async Task a_free_organization_is_offered_the_upgrade_and_refused_and_its_single_repository_saves_as_before()
    {
        var settings = await SettingsAsync();
        Assert.Equal("upgrade", settings.MultiRepoAvailability);
        Assert.False(settings.MultiRepoEnabled);
        Assert.Equal("acme", settings.PrimaryAlias);
        Assert.Empty(settings.AdditionalRepositories!);

        var refused = await PutAsync(true);
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal(ProblemTypes.PlanLimit, await ProblemTypeAsync(refused));
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await PutAsync(additional: [new ProjectRepositoryView("api", 1, null, "/srv/api", null, null)])).StatusCode);

        // The single-repository save, as every client sent it before, is untouched.
        var plain = await PutAsync();
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync(Ct));
        Assert.False((await SettingsAsync()).MultiRepoEnabled);
    }

    [Fact]
    public async Task repositories_and_working_directories_are_validated_saved_and_listed()
    {
        await SeedPaidSubscriptionAsync();
        Assert.Equal("available", (await SettingsAsync()).MultiRepoAvailability);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("Bad Alias!", 1, null, "/srv/x", null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("acme", 1, null, "/srv/x", null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("app", 0, null, null, null, null)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync(true, [new ProjectRepositoryView("app", 1, null, "/srv/app", null, "../outside")])).StatusCode);

        var saved = await PutAsync(true,
            [
                new ProjectRepositoryView("frontend", 0, "acme/frontend", null, null, "./apps/web/"),
                new ProjectRepositoryView("api", 1, null, "/srv/api", "develop", null),
            ],
            primaryWorkingDirectory: "packages/core");
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var settings = await SettingsAsync();
        Assert.True(settings.MultiRepoEnabled);
        Assert.Equal("acme", settings.PrimaryAlias);
        Assert.Equal("packages/core", settings.PrimaryWorkingDirectory);
        Assert.Equal(["frontend", "api"], settings.AdditionalRepositories!.Select(repo => repo.Alias));
        Assert.Equal("apps/web", settings.AdditionalRepositories![0].WorkingDirectory);
        Assert.Equal("main", settings.AdditionalRepositories[0].DefaultBranch);
        Assert.Equal("develop", settings.AdditionalRepositories[1].DefaultBranch);

        // Null leaves the list; an empty one clears it.
        (await PutAsync()).EnsureSuccessStatusCode();
        Assert.Equal(2, (await SettingsAsync()).AdditionalRepositories!.Count);
        (await PutAsync(additional: [])).EnsureSuccessStatusCode();
        Assert.Empty((await SettingsAsync()).AdditionalRepositories!);
    }

    [Fact]
    public async Task playbook_repository_steps_refuse_unknown_waits_and_cycles()
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        Assert.Empty(playbook.RepositorySteps!);
        async Task<HttpStatusCode> PatchAsync(params RepositoryStep[] steps)
        {
            var current = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
            return (await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
                new UpdatePlaybookRequest { RepositorySteps = steps, Version = current.Version }, ApiTestContext.Json, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await PatchAsync(new RepositoryStep("api", null, ["web"])));
        Assert.Equal(HttpStatusCode.BadRequest,
            await PatchAsync(new RepositoryStep("api", null, ["acme"]), new RepositoryStep("acme", null, ["api"])));
        Assert.Equal(HttpStatusCode.OK,
            await PatchAsync(new RepositoryStep("acme"), new RepositoryStep("api", "packages/api/", ["acme"], true)));
        var saved = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        Assert.Equal("packages/api", saved.RepositorySteps![1].WorkingDirectory);
        Assert.True(saved.RepositorySteps[1].WaitForMerge);
    }

    [Fact]
    public async Task an_implement_run_with_no_labels_checks_out_every_repository_and_links_a_pull_request_in_each_changed_one()
    {
        await EnableAsync();

        var run = await DispatchAsync(ItemKey);
        Assert.Equal(["acme", "api"], run.Repositories);
        var prompt = (await RunAsync(run.Id)).PromptSnapshot!;
        Assert.Contains("- `acme`: `repo/acme/` (/srv/acme, default branch main)", prompt);
        Assert.Contains("- `api`: `repo/api/` (/srv/api, default branch develop); work in `repo/api/packages/api`", prompt);
        Assert.Contains("one pull request per changed repository", prompt);
        Assert.Contains("a repository you leave unchanged gets no pull request", prompt);
        Assert.Contains("edit each pull request's description to link the others", prompt);
        Assert.Contains("several checkouts under `repo/`", prompt);

        // An older CLI checks out one repository, and a runner without api's clone leaves the run to
        // one that has them all while that one is online.
        await HelloAsync(Runner.Secret);
        var older = await RegisterRunnerAsync("old-box");
        await HelloAsync(older.Secret, multiRepo: false);
        var partial = await RegisterRunnerAsync("half-box");
        await HelloAsync(partial.Secret, workspaces: ["WEB"]);
        using (var olderClient = RunnerClient(older.Secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(olderClient))!.StatusCode);
        }
        using (var partialClient = RunnerClient(partial.Secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(partialClient))!.StatusCode);
        }
        var waiting = (await RunAsync(run.Id)).Waiting;
        Assert.NotNull(waiting);

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal(["acme", "api"], claimed.Repos!.Select(repo => repo.Alias));
        var api = claimed.Repos!.Single(repo => repo.Alias == "api");
        Assert.Equal(("local", "/srv/api", "develop", "packages/api", false),
            (api.Source, api.LocalPathHint, api.DefaultBranch, api.WorkingDirectory, api.Primary));
        Assert.True(claimed.Repos!.Single(repo => repo.Alias == "acme").Primary);

        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        var finished = await FinishWithAsync(runner, run.Id, "https://github.com/acme/acme/pull/1", "https://github.com/acme/api/pull/2");
        Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync(Ct));
        var done = await RunAsync(run.Id);
        Assert.Equal(["https://github.com/acme/acme/pull/1", "https://github.com/acme/api/pull/2"], done.PullRequestUrls);
        Assert.Null(done.RepositorySteps);
        Assert.Empty(done.MissingRepositories!);

        await SettleAsync(run.Id);
        Assert.Equal(2, await ScalarAsync($"SELECT count(*) FROM work.item_links WHERE item_id = '{Item.Id}' AND kind = 2"));
        var header = (await CommentsAsync(ItemKey)).Items.Last().BodyMarkdown;
        Assert.Contains($"Run {run.Id} succeeded · acme, api · ", header);
    }

    [Fact]
    public async Task a_runner_that_cannot_find_a_checkout_hands_the_run_back_naming_it()
    {
        await EnableAsync();
        var run = await DispatchAsync(ItemKey);
        Assert.Equal(["acme", "api"], run.Repositories);
        await HelloAsync(Runner.Secret);
        using var runner = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(runner)).RunId);

        var wrong = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/release",
            new RunnerReleaseRequest("no-local-repository", "frontend"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var released = await runner.PostAsJsonAsync($"/api/v1/runner/runs/{run.Id}/release",
            new RunnerReleaseRequest("no-local-repository", "api"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Equal(1, await ScalarAsync($"SELECT cardinality(declined_runner_ids) FROM automation.runs WHERE id = '{run.Id}'"));
    }

    [Fact]
    public async Task a_backend_then_frontend_graph_starts_the_second_repository_only_after_the_first_pull_request_merges()
    {
        await EnableAsync();
        await SetStepsAsync(new RepositoryStep("acme"), new RepositoryStep("api", null, ["acme"], true));
        await HelloAsync(Runner.Secret);
        using var runner = RunnerClient(Runner.Secret);

        var first = await DispatchAsync(ItemKey);
        Assert.Equal(["acme"], first.Repositories);
        Assert.Contains("step 1 of 2", (await RunAsync(first.Id)).PromptSnapshot);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(["acme"], claimed.Repos!.Select(repo => repo.Alias));
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, first.Id)).StatusCode);
        const string backendPullRequest = "https://github.com/acme/acme/pull/9";
        (await FinishWithAsync(runner, first.Id, backendPullRequest)).EnsureSuccessStatusCode();
        Assert.True((await StagedRunFinishedAsync(first.Id)).Chained);
        await SettleAsync(first.Id);

        var view = await RunAsync(first.Id);
        Assert.Equal(["succeeded", "waiting-for-merge"], view.RepositorySteps!.Select(step => step.State));

        // Not merged yet: the sweeper leaves it waiting.
        await SweepAsync();
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM automation.runs"));

        await ExecuteSqlAsync($"UPDATE work.item_links SET state = 'merged' WHERE url = '{backendPullRequest}'");
        await SweepAsync();
        var second = await ScalarAsync("SELECT count(*) FROM automation.runs WHERE 'api' = ANY(repository_aliases)");
        Assert.Equal(1, second);
        var next = await ClaimAsync(runner);
        Assert.Equal(["api"], next.Repos!.Select(repo => repo.Alias));
        Assert.Equal(first.BranchName, next.BranchName);
        var nextView = await RunAsync(next.RunId);
        Assert.Contains("step 2 of 2", nextView.PromptSnapshot);
        Assert.Equal(["succeeded", "running"], nextView.RepositorySteps!.Select(step => step.State));
    }

    [Fact]
    public async Task a_sub_package_run_keeps_the_single_checkout_and_starts_in_its_working_directory()
    {
        await SeedPaidSubscriptionAsync();
        (await PutAsync(true, primaryWorkingDirectory: "packages/web")).EnsureSuccessStatusCode();
        await HelloAsync(Runner.Secret, workspaces: ["WEB"]);

        var run = await DispatchAsync(ItemKey);
        Assert.Empty(run.Repositories!);
        Assert.Equal("packages/web", run.WorkingDirectory);
        Assert.Contains("You start in `packages/web`", (await RunAsync(run.Id)).PromptSnapshot);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Null(claimed.Repos);
        Assert.Equal("packages/web", claimed.WorkingDirectory);
    }

    [Fact]
    public async Task a_downgraded_project_keeps_its_repositories_but_runs_in_the_primary_one()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:api");
        await ExecuteSqlAsync($"UPDATE billing.subscriptions SET plan = 'free' WHERE organization_id = '{OrganizationId}'");

        var settings = await SettingsAsync();
        Assert.Equal("upgrade", settings.MultiRepoAvailability);
        Assert.True(settings.MultiRepoEnabled);
        Assert.Single(settings.AdditionalRepositories!);
        // Saving the other settings still works, and keeps the configuration.
        (await PutAsync()).EnsureSuccessStatusCode();
        Assert.Single((await SettingsAsync()).AdditionalRepositories!);

        var run = await DispatchAsync(ItemKey);
        Assert.Empty(run.Repositories!);
        Assert.Null(run.WorkingDirectory);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Null(claimed.Repos);
        Assert.DoesNotContain("# Repositories", claimed.Prompt);

        // So do the read-only kinds.
        var chat = await ChatAsync((await CreateItemAsync("Which versions?")).Key, "which Laravel version is this?");
        Assert.Equal("chat", chat.Kind);
        Assert.Empty(chat.Repositories!);
        Assert.DoesNotContain("# Repositories", chat.PromptSnapshot);
        var refine = await RefineAsync((await CreateItemAsync("Tidy the docs")).Key);
        Assert.Empty(refine.Repositories!);
        Assert.DoesNotContain("# Repositories", refine.PromptSnapshot);
        var projectChat = await ProjectChatAsync("Where is the API?");
        Assert.Empty(projectChat.Repositories!);
        Assert.DoesNotContain("# Repositories", projectChat.PromptSnapshot);
    }

    [Fact]
    public async Task chat_refine_and_project_chat_runs_check_out_every_repository_read_only_and_scheduled_runs_only_the_primary()
    {
        await EnableAsync();
        await HelloAsync(Runner.Secret);

        // The comment from the report: two repositories, one question, no labels.
        var chat = await ChatAsync(ItemKey, "what versions are core-api and pro-app? Check Laravel and Expo please");
        Assert.Equal("chat", chat.Kind);
        Assert.Equal(["acme", "api"], chat.Repositories);
        Assert.Equal("main", chat.BranchName);
        Assert.Contains("Do not change code, create branches, commit, push or open a pull request.", chat.PromptSnapshot);
        Assert.Contains("this run has a read-only checkout of each under `repo/`", chat.PromptSnapshot);
        Assert.Contains("- `api`: `repo/api/` (/srv/api, default branch develop); the code is in `repo/api/packages/api`", chat.PromptSnapshot);
        Assert.Contains("never say one of them is not part of the project or ask anyone to add it", chat.PromptSnapshot);
        Assert.DoesNotContain("one pull request per changed repository", chat.PromptSnapshot);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(chat.Id, claimed.RunId);
        Assert.True(claimed.WorkOnDefaultBranch);
        Assert.Equal(["acme", "api"], claimed.Repos!.Select(repo => repo.Alias));

        var refine = await RefineAsync((await CreateItemAsync("Show the Expo version")).Key);
        Assert.Equal(["acme", "api"], refine.Repositories);
        Assert.Contains("Do not change code, create branches, commit, push or open a pull request.", refine.PromptSnapshot);
        Assert.Contains("- `acme`: `repo/acme/`", refine.PromptSnapshot);

        var projectChat = await ProjectChatAsync("Which Expo version does the app use?");
        Assert.Equal(["acme", "api"], projectChat.Repositories);
        Assert.Contains("The repository is read-only.", projectChat.PromptSnapshot);
        Assert.Contains("- `api`: `repo/api/`", projectChat.PromptSnapshot);

        var later = await CreateItemAsync("Nightly cleanup");
        var scheduled = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{later.Key}/runs",
            new DispatchRunRequest(null, null, ScheduledFor: DateTimeOffset.UtcNow.AddHours(1)), ApiTestContext.Json, Ct);
        Assert.True(scheduled.StatusCode == HttpStatusCode.Created, await scheduled.Content.ReadAsStringAsync(Ct));
        var run = (await scheduled.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Empty(run.Repositories!);
    }

    [Fact]
    public async Task a_plain_label_is_a_focus_hint_and_ordering_labels_still_make_waves()
    {
        await EnableAsync();
        await LabelAsync(ItemKey, "repo:api");
        var hinted = await RunAsync((await DispatchAsync(ItemKey)).Id);
        Assert.Equal(["acme", "api"], hinted.Repositories);
        Assert.Null(hinted.RepositorySteps);
        Assert.Contains("The ticket's labels point at `api`: start there", hinted.PromptSnapshot);

        // A label for a repository the project does not have is only a hint, and ignored.
        var other = await CreateItemAsync("Wire the app");
        await LabelAsync(other.Key, "repo:pro-app");
        var ignored = await RunAsync((await DispatchAsync(other.Key)).Id);
        Assert.Equal(["acme", "api"], ignored.Repositories);
        Assert.DoesNotContain("labels point at", ignored.PromptSnapshot);

        // An ordering label still runs api only after acme, and names only the project's repositories.
        var ordered = await CreateItemAsync("Backend first");
        await LabelAsync(ordered.Key, "repo:api:after:acme");
        var first = await RunAsync((await DispatchAsync(ordered.Key)).Id);
        Assert.Equal(["acme"], first.Repositories);
        Assert.Contains("step 1 of 2", first.PromptSnapshot);
        var unknown = await CreateItemAsync("Unknown order");
        await LabelAsync(unknown.Key, "repo:api:after:web");
        var refused = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{unknown.Key}/runs",
            new DispatchRunRequest(PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("names the repository web", await refused.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task a_runner_with_only_some_repositories_starts_with_those_and_names_the_missing_ones()
    {
        await EnableAsync();
        // The only runner maps acme but not api, and no other runner has both.
        await HelloAsync(Runner.Secret, workspaces: ["WEB"]);

        var run = await DispatchAsync(ItemKey);
        Assert.Equal(["acme", "api"], run.Repositories);
        Assert.Equal("runner-free", (await RunAsync(run.Id)).Waiting!.Reason);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(run.Id, claimed.RunId);
        Assert.Equal(["acme"], claimed.Repos!.Select(repo => repo.Alias));
        Assert.Contains("# Repositories this runner does not have", claimed.Prompt);
        Assert.Contains("`aictiq runner map WEB/api <path>`", claimed.Prompt);
        Assert.Contains("Do not say a missing repository is not part of the project", claimed.Prompt);
        Assert.Equal(["api"], (await RunAsync(run.Id)).MissingRepositories);

        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        (await FinishWithAsync(runner, run.Id, "https://github.com/acme/acme/pull/3")).EnsureSuccessStatusCode();
        await SettleAsync(run.Id);
        var header = (await CommentsAsync(ItemKey)).Items.Last().BodyMarkdown;
        Assert.Contains($"Run {run.Id} succeeded · acme · not checked out, this runner has no clone of api (map it with `aictiq runner map WEB/api <path>`)", header);
    }

    [Fact]
    public async Task a_runner_with_none_of_the_repositories_is_refused_and_an_older_cli_is_refused_for_every_kind()
    {
        await EnableAsync();
        var older = await RegisterRunnerAsync("old-box");
        await HelloAsync(older.Secret, multiRepo: false);
        var empty = await RegisterRunnerAsync("empty-box");
        await HelloAsync(empty.Secret, workspaces: ["OTHER"]);

        var tooOld = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(PlaybookId, AgentId, older.Runner.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooOld.StatusCode);
        Assert.Contains("older Aictiq CLI", await tooOld.Content.ReadAsStringAsync(Ct));
        var none = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(PlaybookId, AgentId, empty.Runner.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("has none of this project's repositories", await none.Content.ReadAsStringAsync(Ct));

        // The project's default runner takes project chat, so its older CLI refuses that too.
        var current = await SettingsAsync();
        (await Owner.PutAsJsonAsync(SettingsUrl,
            new UpdateFactorySettingsRequest(current.RepoSource, current.RepoFullName, current.DefaultBranch,
                current.LocalPathHint, current.DefaultAgentId, current.Version, older.Runner.Id),
            ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var chat = await Owner.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Where is the API?" }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, chat.StatusCode);
        Assert.Contains("older Aictiq CLI", await chat.Content.ReadAsStringAsync(Ct));
    }
}

/// <summary>A self-hosted install does not offer multiple repositories, and refuses to turn them on.</summary>
[Trait("Category", "Automation")]
public sealed class MultiRepoSelfHostedTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    [Fact]
    public async Task the_option_is_hidden_and_the_api_refuses_it()
    {
        var url = $"{ProjectBase}/factory-settings";
        var settings = (await Owner.GetFromJsonAsync<FactorySettingsView>(url, ApiTestContext.Json, Ct))!;
        Assert.Equal("hidden", settings.MultiRepoAvailability);

        var refused = await Owner.PutAsJsonAsync(url,
            new UpdateFactorySettingsRequest(settings.RepoSource, settings.RepoFullName, settings.DefaultBranch,
                settings.LocalPathHint, settings.DefaultAgentId, settings.Version, MultiRepoEnabled: true),
            ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("multiRepoEnabled", out _));
    }
}
