using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A stakeholder sees the work and none of the machinery: no agent's comments or the
/// threads they start, no run's record of itself, and no playbook page.
/// </summary>
[Trait("Category", "Automation")]
public sealed class FactoryVisibilityTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    [Fact]
    public async Task a_stakeholder_does_not_see_agent_comments_or_what_a_run_wrote()
    {
        using var stakeholder = await StakeholderAsync();
        var human = await CommentAsync(Owner, ItemKey, "Customers keep landing on the wrong page.");

        var run = await DispatchAsync(ItemKey);
        using var runner = RunnerClient(Runner.Secret);
        var claimed = await ClaimAsync(runner);
        using var agent = TokenClient(claimed.AgentToken);
        var progress = await CommentAsync(agent, ItemKey, "<!-- aictiq:progress -->\nPlanning: patch the redirect middleware.");
        var answer = await CommentAsync(Owner, ItemKey, "Keep the old route working too.", progress.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await StartedAsync(runner, run.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await FinishAsync(runner, run.Id, RunOutcomes.Succeeded, "Shipped the fix.")).StatusCode);
        await InvokeRunFinishedAsync(await StagedRunFinishedAsync(run.Id));

        // The operator sees everything: their comment, the agent's, the reply to it, the outcome.
        var all = await CommentsAsync(ItemKey);
        Assert.Equal(4, all.Items.Count);

        var seen = (await stakeholder.GetFromJsonAsync<PagedResult<CommentView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/comments/", ApiTestContext.Json, Ct))!;
        Assert.Equal([human.Id], seen.Items.Select(x => x.Id));
        Assert.Equal(1, seen.TotalCount);

        // Nor can they reach one by id.
        var reply = await stakeholder.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/comments",
            new CreateCommentRequest("What does that mean?", progress.Id), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        var react = await stakeholder.PutAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/comments/{answer.Id}/reactions",
            new ReactToCommentRequest("👍"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NotFound, react.StatusCode);

        // Search, the item's history and the agent feed keep the same secret.
        var search = (await stakeholder.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/orgs/{Slug}/search?q=middleware&types=comments", ApiTestContext.Json, Ct))!;
        Assert.Empty(search.Comments);
        var ownerSearch = (await Owner.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/orgs/{Slug}/search?q=middleware&types=comments", ApiTestContext.Json, Ct))!;
        Assert.Single(ownerSearch.Comments);

        var history = (await stakeholder.GetFromJsonAsync<PagedResult<ItemHistoryEventView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/history", ApiTestContext.Json, Ct))!;
        Assert.DoesNotContain(history.Items.SelectMany(x => x.Changes), x => x.Field == "run-finished");
        var ownerHistory = (await Owner.GetFromJsonAsync<PagedResult<ItemHistoryEventView>>(
            $"/api/v1/orgs/{Slug}/items/{ItemKey}/history", ApiTestContext.Json, Ct))!;
        Assert.Contains(ownerHistory.Items.SelectMany(x => x.Changes), x => x.Field == "run-finished");

        var feed = (await stakeholder.GetFromJsonAsync<List<AgentActivityEntry>>(
            $"/api/v1/orgs/{Slug}/agent-activity/", ApiTestContext.Json, Ct))!;
        Assert.DoesNotContain(feed, x => x.Kind == "commented" && x.Actor?.Id == AgentId);
    }

    [Fact]
    public async Task a_stakeholder_does_not_see_the_factory_section_or_a_playbook_page_outside_it()
    {
        var playbook = (await Owner.GetFromJsonAsync<PlaybookView>($"{ProjectBase}/playbooks/{PlaybookId}", ApiTestContext.Json, Ct))!;
        var implement = playbook.WikiPageId!.Value;
        var handbook = await CreatePageAsync("Handbook");
        var legacy = await CreatePageAsync("Review checklist");
        var written = await Owner.PostAsJsonAsync($"{ProjectBase}/playbooks",
            new CreatePlaybookRequest("Review", null, "claude", null, null, 60, "Check the checklist."), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        var review = (await written.Content.ReadFromJsonAsync<PlaybookView>(ApiTestContext.Json, Ct))!;
        // A playbook from before its page had to be in the Factory section.
        await ExecuteSqlAsync($"UPDATE automation.playbooks SET wiki_page_id = '{legacy}' WHERE id = '{PlaybookId}'");

        using var stakeholder = await StakeholderAsync();
        var tree = await TreeAsync(stakeholder);
        Assert.Equal([handbook], tree);
        Assert.DoesNotContain(review.WikiPageId!.Value, tree);
        Assert.Equal(HttpStatusCode.NotFound, (await stakeholder.GetAsync($"/api/v1/orgs/{Slug}/wiki/pages/{implement}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stakeholder.GetAsync($"/api/v1/orgs/{Slug}/wiki/pages/{legacy}", Ct)).StatusCode);
        var search = (await stakeholder.GetFromJsonAsync<SearchResponse>(
            $"/api/v1/orgs/{Slug}/search?q=checklist&types=pages", ApiTestContext.Json, Ct))!;
        Assert.Empty(search.Pages);

        var ownerTree = await TreeAsync(Owner);
        Assert.Contains(implement, ownerTree);
        Assert.Contains(review.WikiPageId!.Value, ownerTree);
        Assert.Contains(legacy, ownerTree);
    }

    private async Task<HttpClient> StakeholderAsync()
    {
        var auth = await Context.RegisterAsync($"stakeholder-{Guid.NewGuid():N}@test.local", "Sam", "Stakeholder");
        await AddMemberAsync(auth.User.Id, OrgRole.Member, canOperateFactory: false);
        return Context.ClientFor(auth);
    }

    private async Task<CommentView> CommentAsync(HttpClient client, string itemKey, string body, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/comments",
            new CreateCommentRequest(body, parentId), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<Guid> CreatePageAsync(string title)
    {
        var response = await Owner.PostAsJsonAsync($"{ProjectBase}/wiki/pages",
            new CreateWikiPageRequest(null, title, $"# {title}\n\nThe {title.ToLowerInvariant()}."), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WikiPageView>(ApiTestContext.Json, Ct))!.Id;
    }

    private async Task<List<Guid>> TreeAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<WikiTreePageView>>($"{ProjectBase}/wiki/tree", ApiTestContext.Json, Ct))!
            .Select(x => x.Id).ToList();
}
