using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Paging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// The rule conveyor: the whole point is that nobody clicks Start - an item
/// entering a state does. These tests exercise <c>RuleFiringHandler</c> directly, the way
/// <see cref="RunOutcomeTests"/> exercises <c>RunFinishedHandler</c>: the item transitions
/// through the real HTTP endpoints (so the item history and the event shape are real), and
/// the resulting <see cref="WorkItemTransitioned"/> is handed to the handler the way the
/// outbox would - including redelivering the very same event to prove the replay guard.
/// </summary>
[Trait("Category", "Automation")]
public sealed class RuleTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private string RulesBase => $"{ProjectBase}/rules";

    [Fact]
    public async Task crud_and_authorization()
    {
        var reviewState = await StateIdAsync("In Review");

        var create = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest("Auto-verify", reviewState, null, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync(Ct));
        var rule = (await create.Content.ReadFromJsonAsync<RuleView>(ApiTestContext.Json, Ct))!;
        Assert.Equal("Auto-verify", rule.Name);
        Assert.Equal(reviewState, rule.TriggerStateId);
        Assert.True(rule.Enabled);
        Assert.Null(rule.LastFiring);
        Assert.Equal(Project.Key, rule.ProjectKey);

        var get = await Owner.GetFromJsonAsync<RuleView>($"{RulesBase}/{rule.Id}", ApiTestContext.Json, Ct);
        Assert.Equal(rule.Id, get!.Id);

        var patched = await Owner.PatchAsJsonAsync($"{RulesBase}/{rule.Id}",
            new { name = "Auto-verify v2", version = rule.Version }, ApiTestContext.Json, Ct);
        Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync(Ct));
        var updated = (await patched.Content.ReadFromJsonAsync<RuleView>(ApiTestContext.Json, Ct))!;
        Assert.Equal("Auto-verify v2", updated.Name);

        // A stale version 409s.
        var stale = await Owner.PatchAsJsonAsync($"{RulesBase}/{rule.Id}",
            new { name = "Auto-verify v3", version = rule.Version }, ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // A plain org Member is an implicit project Member on this org-visible project -
        // not Admin, so this is 403, not 404.
        var memberAuth = await Context.RegisterAsync($"rule-member-{Guid.NewGuid():N}@test.local");
        using (var member = Context.ClientFor(memberAuth))
        {
            await AddMemberAsync(memberAuth.User.Id, OrgRole.Member, false);
            var forbidden = await member.GetAsync(RulesBase, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(ProblemTypes.InsufficientRole, await ProblemTypeAsync(forbidden));
        }

        // A stranger is not a member at all: 404, never 403 - existence is not confirmed.
        using (var stranger = Context.ClientFor(await Context.RegisterAsync($"rule-stranger-{Guid.NewGuid():N}@test.local")))
        {
            var notFound = await stranger.GetAsync(RulesBase, Ct);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        // A stakeholder: explicit project Admin, but an org Member whose factory flag is
        // off - Admin gets them past RequireProjectRole, the flag stops them right after.
        var stakeholderAuth = await Context.RegisterAsync($"rule-stakeholder-{Guid.NewGuid():N}@test.local");
        using var stakeholder = Context.ClientFor(stakeholderAuth);
        await AddMemberAsync(stakeholderAuth.User.Id, OrgRole.Member, false);
        (await Owner.PutAsJsonAsync($"{ProjectBase}/members/{stakeholderAuth.User.Id}",
            new UpdateProjectMemberRequest(ProjectRole.Admin), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var stakeholderResponse = await stakeholder.GetAsync(RulesBase, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, stakeholderResponse.StatusCode);
        Assert.Equal(ProblemTypes.FactoryNotPermitted, await ProblemTypeAsync(stakeholderResponse));

        // Archived project: 409, not 403 - the caller's role is fine.
        (await Owner.PostAsync($"{ProjectBase}/archive", null, Ct)).EnsureSuccessStatusCode();
        var archived = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest("Another", reviewState, null, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
        Assert.Equal(ProblemTypes.ProjectArchived, await ProblemTypeAsync(archived));
        (await Owner.PostAsync($"{ProjectBase}/unarchive", null, Ct)).EnsureSuccessStatusCode();

        var delete = await Owner.DeleteAsync($"{RulesBase}/{rule.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner.GetAsync($"{RulesBase}/{rule.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task rule_cannot_name_a_state_a_label_or_a_playbook_of_another_project()
    {
        var otherProject = await CreateProjectAsync("Other", "oth", ProjectVisibility.Organization);
        var otherState = await StateIdAsync("In Review", otherProject.Key);
        var reviewState = await StateIdAsync("In Review");

        var wrongState = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest("Bad state", otherState, null, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);

        var otherLabel = await CreateLabelAsync("urgent", otherProject.Key);
        var wrongLabel = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest("Bad label", reviewState, otherLabel, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongLabel.StatusCode);

        var wrongPlaybook = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest("Bad playbook", reviewState, null, Guid.NewGuid(), AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongPlaybook.StatusCode);
    }

    [Fact]
    public async Task transition_into_the_trigger_state_dispatches_a_run_and_the_firing_records_it()
    {
        var item = await CreateItemAsync("Ship the login fix");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Auto-verify", reviewState, PlaybookId, AgentId);

        var fromState = (await ItemAsync(item.Key)).StateId;
        var transitioned = await TransitionAsync(item.Key, reviewState, item.Version);
        var @event = new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, fromState, reviewState, OwnerId);

        await InvokeRuleFiringAsync(@event);

        var runs = await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct);
        var run = Assert.Single(runs!.Items);
        Assert.Equal(rule.Id, run.RuleId);
        Assert.Equal("Auto-verify", run.RuleName);
        Assert.Null(run.RequestedBy);
        Assert.Equal(AgentId, run.AgentId);

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        var firing = Assert.Single(firings!);
        Assert.Equal(run.Id, firing.RunId);
        Assert.Equal("queued", firing.RunStatus);
        Assert.Null(firing.SkipReason);

        // Replaying the very same event (same EventId) must not start a second run.
        await InvokeRuleFiringAsync(@event);
        var runsAfterReplay = await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct);
        Assert.Single(runsAfterReplay!.Items);
    }

    [Fact]
    public async Task every_transition_handler_runs_without_a_request_tenant_and_the_rule_still_fires()
    {
        // The outbox delivers an event to all of its handlers, in Workers, with no request
        // tenant; one handler that throws fails the delivery for all of them. Analytics once
        // did exactly that on every transition, so no rule ever fired outside these tests.
        var item = await CreateItemAsync("Delivered like the outbox would");
        var reviewState = await StateIdAsync("In Review");
        await CreateRuleAsync("Every handler", reviewState, PlaybookId, AgentId);
        var fromState = (await ItemAsync(item.Key)).StateId;
        var transitioned = await TransitionAsync(item.Key, reviewState, item.Version);
        var @event = new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, fromState, reviewState, OwnerId);

        // Analytics and Automation: the handlers whose dependencies the API's container can
        // build (Notifications' needs its Workers-only mail services, and has tests of its own).
        var handlerTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "Aictiq.Modules.Analytics" or "Aictiq.Modules.Automation")
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IDomainEventHandler<WorkItemTransitioned>).IsAssignableFrom(type))
            .ToList();
        Assert.Contains(handlerTypes, type => type.Name == "RuleFiringHandler");
        Assert.Contains(handlerTypes, type => type.Name == "TransitionAnalyticsEventHandler");
        foreach (var type in handlerTypes)
        {
            using var scope = Context.Factory.Services.CreateScope();
            var handler = (IDomainEventHandler<WorkItemTransitioned>)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
            await handler.HandleAsync(@event, Ct);
        }

        var runs = await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct);
        Assert.Equal("Every handler", Assert.Single(runs!.Items).RuleName);
    }

    [Fact]
    public async Task a_claimed_item_is_skipped_and_recorded()
    {
        var item = await CreateItemAsync("Investigate the outage");
        var reviewState = await StateIdAsync("In Review");
        var activeState = await StateIdAsync("Active");
        await DispatchAsync(item.Key); // Claims the item and moves it to Active (no event, by design).

        var claimed = await ItemAsync(item.Key);
        var transitioned = await TransitionAsync(item.Key, reviewState, claimed.Version);

        var rule = await CreateRuleAsync("Auto-verify", reviewState, PlaybookId, AgentId);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, activeState, reviewState, OwnerId));

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        var firing = Assert.Single(firings!);
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.ItemClaimed, firing.SkipReason);
    }

    [Fact]
    public async Task a_disabled_rule_fires_nothing()
    {
        var item = await CreateItemAsync("Quietly disabled");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Off", reviewState, PlaybookId, AgentId, enabled: false);

        var current = await ItemAsync(item.Key);
        var transitioned = await TransitionAsync(item.Key, reviewState, current.Version);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, current.StateId, reviewState, OwnerId));

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        Assert.Empty(firings!);
        var runs = await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct);
        Assert.Empty(runs!.Items);
    }

    [Fact]
    public async Task a_required_label_gates_the_firing()
    {
        var item = await CreateItemAsync("Needs the agent label");
        var reviewState = await StateIdAsync("In Review");
        var labelId = await CreateLabelAsync("agent");
        var rule = await CreateRuleAsync("Label-gated", reviewState, PlaybookId, AgentId, requiredLabelId: labelId);

        var before = await ItemAsync(item.Key);
        var transitioned = await TransitionAsync(item.Key, reviewState, before.Version);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, before.StateId, reviewState, OwnerId));

        // No label: no firing row at all, not even a skip.
        Assert.Empty((await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct))!);

        (await Owner.PutAsync($"/api/v1/orgs/{Slug}/items/{item.Key}/labels/{labelId}", null, Ct)).EnsureSuccessStatusCode();
        var backToNew = await StateIdAsync("New");
        var afterLabel = await ItemAsync(item.Key);
        var second = await TransitionAsync(item.Key, backToNew, afterLabel.Version);
        var toReview = await ItemAsync(item.Key);
        var third = await TransitionAsync(item.Key, reviewState, toReview.Version);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, third.Id, third.Key, backToNew, reviewState, OwnerId));

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        var firing = Assert.Single(firings!);
        Assert.NotNull(firing.RunId);
    }

    [Fact]
    public async Task a_rule_whose_playbook_page_was_deleted_skips()
    {
        var item = await CreateItemAsync("Orphaned playbook");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Orphaned", reviewState, PlaybookId, AgentId);

        await ExecuteSqlAsync($"UPDATE automation.playbooks SET wiki_page_id = NULL WHERE id = '{PlaybookId}'");

        var before = await ItemAsync(item.Key);
        var transitioned = await TransitionAsync(item.Key, reviewState, before.Version);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, before.StateId, reviewState, OwnerId));

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        var firing = Assert.Single(firings!);
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.PlaybookPageMissing, firing.SkipReason);
    }

    [Fact]
    public async Task the_loop_guard_stops_a_rule_from_redispatching_its_own_run()
    {
        var item = await CreateItemAsync("Loops back to review");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Reviewer", reviewState, PlaybookId, AgentId);

        var before = await ItemAsync(item.Key);
        var transitioned = await TransitionAsync(item.Key, reviewState, before.Version);
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, before.StateId, reviewState, OwnerId));

        var firstRun = Assert.Single((await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct))!.Items);
        Assert.Equal(rule.Id, firstRun.RuleId);

        // The agent's own run "finishing" and landing the item back in the same state it
        // dispatched from, as the agent - the guard must recognise its own last run.
        var elsewhere = await StateIdAsync("New");
        await InvokeRuleFiringAsync(new WorkItemTransitioned(
            OrganizationId, Project.Id, transitioned.Id, transitioned.Key, elsewhere, reviewState, AgentId));

        var firings = await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{rule.Id}/firings", ApiTestContext.Json, Ct);
        var loopFiring = Assert.Single(firings!, firing => firing.SkipReason is not null);
        Assert.Equal(RuleSkipReasons.RuleLoop, loopFiring.SkipReason);
        Assert.Single((await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{item.Key}/runs", ApiTestContext.Json, Ct))!.Items);
    }

    [Fact]
    public async Task moving_two_items_at_once_starts_two_runs()
    {
        var itemA = await CreateItemAsync("Bulk item A");
        var itemB = await CreateItemAsync("Bulk item B");
        var reviewState = await StateIdAsync("In Review");
        await CreateRuleAsync("Bulk", reviewState, PlaybookId, AgentId);

        var bulk = await Owner.PostAsJsonAsync($"{ProjectBase}/items/bulk",
            new BulkUpdateItemsRequest(
                [itemA.Key, itemB.Key],
                new BulkItemSet(reviewState, null, null, null, null, null, null),
                new Dictionary<string, uint> { [itemA.Key] = itemA.Version, [itemB.Key] = itemB.Version }),
            ApiTestContext.Json, Ct);
        Assert.True(bulk.IsSuccessStatusCode, await bulk.Content.ReadAsStringAsync(Ct));

        foreach (var (key, id) in new[] { (itemA.Key, itemA.Id), (itemB.Key, itemB.Id) })
        {
            await InvokeRuleFiringAsync(new WorkItemTransitioned(
                OrganizationId, Project.Id, id, key, itemA.StateId, reviewState, OwnerId));
        }

        Assert.Single((await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{itemA.Key}/runs", ApiTestContext.Json, Ct))!.Items);
        Assert.Single((await Owner.GetFromJsonAsync<PagedResult<RunView>>(
            $"/api/v1/orgs/{Slug}/items/{itemB.Key}/runs", ApiTestContext.Json, Ct))!.Items);
    }

    [Fact]
    public async Task database_integrity_constraints()
    {
        // ck_runs_requested_by_xor_rule: neither, or both, is refused.
        var neither = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(requestedBy: null, ruleId: null));
        Assert.Equal(PostgresErrorCodes.CheckViolation, neither.SqlState);
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Integrity", reviewState, PlaybookId, AgentId);
        var both = await Assert.ThrowsAsync<PostgresException>(() => InsertRunAsync(requestedBy: OwnerId, ruleId: rule.Id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, both.SqlState);

        // ux_rules_project_name: case-insensitively unique per project.
        var duplicate = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest(rule.Name.ToUpperInvariant(), reviewState, null, PlaybookId, AgentId), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task the_transition_a_person_makes_fires_the_rule_and_a_runner_takes_the_run_to_the_success_state()
    {
        // The whole belt on the events the system really stages, not hand-built ones: the
        // transition endpoint's outbox row fires the rule, a runner claims and finishes the
        // run, and the finish's own RunFinished moves the item on and lets it go.
        var item = await CreateItemAsync("Rule to runner to resolved");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Belt", reviewState, PlaybookId, AgentId);
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        var successState = playbook.OnSuccessStateId ?? await StateIdAsync("Resolved");

        await TransitionAsync(item.Key, reviewState, item.Version);
        var staged = Assert.Single(await StagedTransitionsAsync(item.Id));
        Assert.Equal(reviewState, staged.ToStateId);
        Assert.Equal(OwnerId, staged.ActorId);
        await InvokeRuleFiringAsync(staged);

        var firing = Assert.Single((await FiringsAsync(rule.Id)));
        Assert.NotNull(firing.RunId);
        var queued = await ItemAsync(item.Key);
        Assert.Equal(AgentId, queued.ClaimedBy);

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(firing.RunId, claimed.RunId);
        Assert.Equal(item.Key, claimed.ItemKey);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, claimed.RunId)).StatusCode);
        Assert.Equal("running", Assert.Single(await FiringsAsync(rule.Id)).RunStatus);
        Assert.Equal(HttpStatusCode.OK, (await FinishAsync(runner, claimed.RunId, RunOutcomes.Succeeded, "Done.")).StatusCode);
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(claimed.RunId));

        var after = await ItemAsync(item.Key);
        Assert.Equal(successState, after.StateId);
        Assert.Null(after.ClaimedBy);
        Assert.Equal("succeeded", Assert.Single(await FiringsAsync(rule.Id)).RunStatus);
    }

    [Fact]
    public async Task a_failed_run_that_lands_back_in_the_trigger_state_is_released_and_not_redispatched()
    {
        // A playbook whose failure state is the rule's own trigger state: the failed run
        // moves the item straight back in, as the agent, and the loop guard must stop there.
        // A person moving it in again afterwards is a new request and fires as usual.
        var item = await CreateItemAsync("Fails back into review");
        var reviewState = await StateIdAsync("In Review");
        var newState = await StateIdAsync("New");
        await SetFailureStateAsync(reviewState);
        var rule = await CreateRuleAsync("Retry on review", reviewState, PlaybookId, AgentId);

        await TransitionAsync(item.Key, reviewState, item.Version);
        await InvokeRuleFiringAsync(Assert.Single(await StagedTransitionsAsync(item.Id)));

        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, claimed.RunId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await FinishAsync(runner, claimed.RunId, RunOutcomes.Failed,
            failureReason: "exit code 1")).StatusCode);
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(claimed.RunId));

        var failed = await ItemAsync(item.Key);
        Assert.Equal(reviewState, failed.StateId);
        Assert.Null(failed.ClaimedBy);

        // The run's own transition back, as the outbox staged it: the agent is its actor.
        var back = (await StagedTransitionsAsync(item.Id)).Last();
        Assert.Equal(AgentId, back.ActorId);
        Assert.Equal(reviewState, back.ToStateId);
        await InvokeRuleFiringAsync(back);

        var loop = Assert.Single(await FiringsAsync(rule.Id), firing => firing.SkipReason is not null);
        Assert.Equal(RuleSkipReasons.RuleLoop, loop.SkipReason);
        Assert.Single((await RunsOfAsync(item.Key)).Items);
        Assert.Null((await ItemAsync(item.Key)).ClaimedBy);

        var moved = await TransitionAsync(item.Key, newState, failed.Version);
        await TransitionAsync(item.Key, reviewState, moved.Version);
        await InvokeRuleFiringAsync((await StagedTransitionsAsync(item.Id)).Last());
        Assert.Equal(2, (await RunsOfAsync(item.Key)).Items.Count);
        Assert.Equal(2, (await FiringsAsync(rule.Id)).Count(firing => firing.RunId is not null));
    }

    [Fact]
    public async Task a_label_added_after_the_item_entered_the_state_fires_nothing()
    {
        // Only entering the state fires a rule. Labelling an item that is already there is an
        // ItemChanged, not a WorkItemTransitioned, so the rule never hears of it.
        var item = await CreateItemAsync("Labelled too late");
        var reviewState = await StateIdAsync("In Review");
        var labelId = await CreateLabelAsync("agent");
        var rule = await CreateRuleAsync("Label-gated", reviewState, PlaybookId, AgentId, requiredLabelId: labelId);

        await TransitionAsync(item.Key, reviewState, item.Version);
        await InvokeRuleFiringAsync(Assert.Single(await StagedTransitionsAsync(item.Id)));
        (await Owner.PutAsync($"/api/v1/orgs/{Slug}/items/{item.Key}/labels/{labelId}", null, Ct)).EnsureSuccessStatusCode();

        Assert.Single(await StagedTransitionsAsync(item.Id));
        Assert.Empty(await FiringsAsync(rule.Id));
        Assert.Empty((await RunsOfAsync(item.Key)).Items);
    }

    [Fact]
    public async Task an_archived_project_records_project_read_only_and_starts_nothing()
    {
        var item = await CreateItemAsync("Moved, then archived");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Archived", reviewState, PlaybookId, AgentId);
        await TransitionAsync(item.Key, reviewState, item.Version);
        var staged = Assert.Single(await StagedTransitionsAsync(item.Id));

        // Archived between the move and the delivery - the outbox runs later, in Workers.
        (await Owner.PostAsync($"{ProjectBase}/archive", null, Ct)).EnsureSuccessStatusCode();
        await InvokeRuleFiringAsync(staged);

        var firing = Assert.Single(await FiringsAsync(rule.Id));
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.ProjectReadOnly, firing.SkipReason);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM automation.runs"));
    }

    [Fact]
    public async Task an_inactive_agent_records_agent_unavailable()
    {
        var item = await CreateItemAsync("Agent switched off");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Switched off", reviewState, PlaybookId, AgentId);
        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/agents/{AgentId}",
            new UpdateAgentRequest(null, false), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();

        await TransitionAsync(item.Key, reviewState, item.Version);
        await InvokeRuleFiringAsync(Assert.Single(await StagedTransitionsAsync(item.Id)));

        var firing = Assert.Single(await FiringsAsync(rule.Id));
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.AgentUnavailable, firing.SkipReason);
        Assert.Null((await ItemAsync(item.Key)).ClaimedBy);
    }

    [Fact]
    public async Task a_live_run_whose_item_claim_was_let_go_records_run_in_progress()
    {
        // The claim usually refuses first (item-claimed). Once a stale claim is released
        // while its run still waits in the queue, the claim succeeds and only the live-run
        // index is left to refuse the second run.
        var item = await CreateItemAsync("Queued, claim let go");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Second run", reviewState, PlaybookId, AgentId);
        await DispatchAsync(item.Key);
        await ExecuteSqlAsync($"UPDATE work.items SET claimed_by = NULL, claimed_at = NULL, claim_heartbeat_at = NULL WHERE id = '{item.Id}'");

        var current = await ItemAsync(item.Key);
        await TransitionAsync(item.Key, reviewState, current.Version);
        await InvokeRuleFiringAsync((await StagedTransitionsAsync(item.Id)).Last());

        var firing = Assert.Single(await FiringsAsync(rule.Id));
        Assert.Null(firing.RunId);
        Assert.Equal(RuleSkipReasons.RunInProgress, firing.SkipReason);
        Assert.Single((await RunsOfAsync(item.Key)).Items);
    }

    [Fact]
    public async Task the_same_event_delivered_twice_at_once_starts_one_run()
    {
        // The replay guard under a race, not only in sequence: two Workers delivering the
        // same outbox row together must still leave one firing and one run.
        var item = await CreateItemAsync("Delivered twice");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Once", reviewState, PlaybookId, AgentId);
        await TransitionAsync(item.Key, reviewState, item.Version);
        var staged = Assert.Single(await StagedTransitionsAsync(item.Id));

        await Task.WhenAll(InvokeRuleFiringAsync(staged), InvokeRuleFiringAsync(staged));
        await InvokeRuleFiringAsync(staged);

        var firing = Assert.Single(await FiringsAsync(rule.Id));
        Assert.NotNull(firing.RunId);
        Assert.Single((await RunsOfAsync(item.Key)).Items);
    }

    [Fact]
    public async Task a_bulk_move_queues_one_run_per_item_and_runners_take_them_all()
    {
        var items = new[]
        {
            await CreateItemAsync("Bulk belt A"), await CreateItemAsync("Bulk belt B"), await CreateItemAsync("Bulk belt C"),
        };
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Bulk belt", reviewState, PlaybookId, AgentId);

        var bulk = await Owner.PostAsJsonAsync($"{ProjectBase}/items/bulk",
            new BulkUpdateItemsRequest(
                items.Select(item => item.Key).ToList(),
                new BulkItemSet(reviewState, null, null, null, null, null, null),
                items.ToDictionary(item => item.Key, item => item.Version)),
            ApiTestContext.Json, Ct);
        Assert.True(bulk.IsSuccessStatusCode, await bulk.Content.ReadAsStringAsync(Ct));
        foreach (var item in items)
        {
            await InvokeRuleFiringAsync(Assert.Single(await StagedTransitionsAsync(item.Id)));
        }

        var firings = await FiringsAsync(rule.Id);
        Assert.Equal(3, firings.Count);
        Assert.All(firings, firing => Assert.NotNull(firing.RunId));

        // Two runners drain the queue between them; each run is handed out exactly once.
        var secondRunner = await RegisterRunnerAsync("box-2");
        using var first = RunnerClient(Runner.Secret);
        using var second = RunnerClient(secondRunner.Secret);
        var claims = new List<RunnerRunClaimed> { await ClaimAsync(first), await ClaimAsync(second), await ClaimAsync(first) };
        Assert.Equal(firings.Select(firing => firing.RunId!.Value).Order(), claims.Select(claim => claim.RunId).Order());
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(second))!.StatusCode);
    }

    [Fact]
    public async Task a_rule_run_waits_for_a_runner_that_can_take_it()
    {
        // No runner online is just no runner polling: the run stays queued. A runner without
        // the playbook's harness is not given it; the first that has it, connecting later, is.
        var item = await CreateItemAsync("Nobody home yet");
        var reviewState = await StateIdAsync("In Review");
        var rule = await CreateRuleAsync("Waits", reviewState, PlaybookId, AgentId);
        await TransitionAsync(item.Key, reviewState, item.Version);
        await InvokeRuleFiringAsync(Assert.Single(await StagedTransitionsAsync(item.Id)));
        var runId = Assert.Single(await FiringsAsync(rule.Id)).RunId!.Value;
        Assert.Equal("queued", (await RunAsync(runId)).Status);

        using var codexOnly = RunnerClient(Runner.Secret);
        var refused = await codexOnly.PostAsJsonAsync("/api/v1/runner/runs/claim",
            new RunnerClaimRequest(["codex"], 1), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);
        Assert.Equal("queued", (await RunAsync(runId)).Status);

        var late = await RegisterRunnerAsync("late-box");
        using var lateRunner = RunnerClient(late.Secret);
        var claimed = await ClaimAsync(lateRunner);
        Assert.Equal(runId, claimed.RunId);
        Assert.Equal("late-box", (await RunAsync(runId)).RunnerName);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private async Task<List<RuleFiringView>> FiringsAsync(Guid ruleId) =>
        (await Owner.GetFromJsonAsync<List<RuleFiringView>>($"{RulesBase}/{ruleId}/firings", ApiTestContext.Json, Ct))!;

    private async Task<PagedResult<RunView>> RunsOfAsync(string itemKey) =>
        (await Owner.GetFromJsonAsync<PagedResult<RunView>>($"/api/v1/orgs/{Slug}/items/{itemKey}/runs", ApiTestContext.Json, Ct))!;

    /// <summary>Every WorkItemTransitioned the item's moves staged in the outbox, oldest first.</summary>
    private async Task<List<WorkItemTransitioned>> StagedTransitionsAsync(Guid itemId)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload FROM shared.outbox_messages
            WHERE type = @type AND payload::jsonb ->> 'ItemId' = @itemId
            ORDER BY occurred_at, id
            """, connection);
        command.Parameters.AddWithValue("type", typeof(WorkItemTransitioned).FullName!);
        command.Parameters.AddWithValue("itemId", itemId.ToString());
        var staged = new List<WorkItemTransitioned>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            staged.Add(JsonSerializer.Deserialize<WorkItemTransitioned>(reader.GetString(0))!);
        }
        return staged;
    }

    private async Task SetFailureStateAsync(Guid stateId)
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        var response = await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
            new { onFailureStateId = stateId, version = playbook.Version }, ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<RuleView> CreateRuleAsync(
        string name, Guid triggerStateId, Guid playbookId, string agentId, Guid? requiredLabelId = null, bool enabled = true)
    {
        var response = await Owner.PostAsJsonAsync(RulesBase,
            new CreateRuleRequest(name, triggerStateId, requiredLabelId, playbookId, agentId, enabled),
            ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RuleView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<WorkItemView> TransitionAsync(string itemKey, Guid toStateId, uint version)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/transition",
            new TransitionRequest(toStateId, version), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<Guid> CreateLabelAsync(string name, string? projectKey = null)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{projectKey ?? Project.Key}/labels",
            new CreateLabelRequest(name, null, null, null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LabelView>(ApiTestContext.Json, Ct))!.Id;
    }

    private async Task<Guid> StateIdAsync(string name, string projectKey)
    {
        var workflows = (await Owner.GetFromJsonAsync<List<WorkflowView>>(
            $"/api/v1/orgs/{Slug}/projects/{projectKey}/workflows/", ApiTestContext.Json, Ct))!;
        return workflows.Single(workflow => workflow.IsDefault).States.Single(state => state.Name == name).Id;
    }

    private async Task InvokeRuleFiringAsync(WorkItemTransitioned @event)
    {
        using var scope = Context.Factory.Services.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<Aictiq.Modules.Automation.Events.RuleFiringHandler>(scope.ServiceProvider);
        await handler.HandleAsync(@event, Ct);
    }

    private async Task InsertRunAsync(string? requestedBy, Guid? ruleId)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO automation.runs
                (id, organization_id, project_id, item_id, item_key, playbook_id, agent_user_id,
                 requested_by, rule_id, status, harness, prompt_snapshot, branch_name, max_minutes, queued_at)
            VALUES
                (gen_random_uuid(), @org, @project, gen_random_uuid(), 'WEB-1', @playbook, @agent,
                 @requested_by, @rule_id, 0, 'claude', 'x', 'b', 60, now())
            """, connection);
        command.Parameters.AddWithValue("org", OrganizationId);
        command.Parameters.AddWithValue("project", Project.Id);
        command.Parameters.AddWithValue("playbook", PlaybookId);
        command.Parameters.AddWithValue("agent", AgentId);
        command.Parameters.AddWithValue("requested_by", (object?)requestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("rule_id", (object?)ruleId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
