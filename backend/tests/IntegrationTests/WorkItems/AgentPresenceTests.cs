using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// What the presence surfaces read: the <c>claimed:</c> filter and the organization's
/// agent activity feed.
///
/// The distinction these protect is claim-versus-assignment. A claim is a lease an agent
/// holds while it works, and "what is an agent doing right now" cannot be answered from
/// the assignee column: an item can be assigned to someone and claimed by nobody, and an
/// abandoned claim outlives the agent that made it.
/// </summary>
[Trait("Category", "WorkItems")]
public sealed class AgentPresenceTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task the_claimed_filter_separates_held_items_from_merely_assigned_ones()
    {
        var claimed = await CreateAsync(WorkItemType.Bug, "Held");
        var free = await CreateAsync(WorkItemType.Bug, "Free");
        var response = await Client.PostAsJsonAsync(ItemPath(claimed.Key, "claim"),
            new ClaimRequest(claimed.Version), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.Equal([claimed.Key], await KeysAsync("claimed:any"));
        Assert.Contains(free.Key, await KeysAsync("claimed:none"));
        Assert.DoesNotContain(claimed.Key, await KeysAsync("claimed:none"));
        Assert.Equal([claimed.Key], await KeysAsync("claimed:@me"));

        // The claimant here is a person, so the agent filter must not match them - this is
        // the assertion that stops `claimed:@agent` degrading into `claimed:any`.
        Assert.Empty(await KeysAsync("claimed:@agent"));
    }

    [Fact]
    public async Task an_unknown_claimed_value_is_a_validation_error_not_an_empty_list()
    {
        var response = await Client.GetAsync(
            $"/api/v1/orgs/work-items/projects/{Project.Key}/items/?filter=claimed%3Asometimes", CancellationToken);

        // A silently-empty list would read as "nothing is claimed", which is a different
        // and much more misleading answer than "that filter makes no sense".
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task the_activity_feed_merges_changes_and_comments_newest_first()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Watched");
        var renamed = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, "Watched, renamed", null, null, null, null, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        renamed.EnsureSuccessStatusCode();
        var commented = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/items/{item.Key}/comments/",
            new CreateCommentRequest("<!-- aictiq:progress -->\nWorking on it."), ApiTestContext.Json, CancellationToken);
        commented.EnsureSuccessStatusCode();

        var feed = await Client.GetFromJsonAsync<List<AgentActivityEntry>>(
            "/api/v1/orgs/work-items/agent-activity/", ApiTestContext.Json, CancellationToken);

        Assert.NotNull(feed);
        Assert.Contains(feed, x => x.Kind == "changed" && x.ItemKey == item.Key);
        var comment = Assert.Single(feed, x => x.Kind == "commented" && x.ItemKey == item.Key);
        // The marker an agent uses to find its own progress comment must not become the
        // whole summary - every one of its rows would then look blank.
        Assert.Equal("Working on it.", comment.Summary);
        Assert.Equal(feed.OrderByDescending(x => x.At).Select(x => x.At), feed.Select(x => x.At));
    }

    [Fact]
    public async Task agents_only_hides_everything_a_person_did()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Person's work");
        var commented = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/items/{item.Key}/comments/",
            new CreateCommentRequest("A person wrote this."), ApiTestContext.Json, CancellationToken);
        commented.EnsureSuccessStatusCode();

        var all = await Client.GetFromJsonAsync<List<AgentActivityEntry>>(
            "/api/v1/orgs/work-items/agent-activity/", ApiTestContext.Json, CancellationToken);
        var agentsOnly = await Client.GetFromJsonAsync<List<AgentActivityEntry>>(
            "/api/v1/orgs/work-items/agent-activity/?agentsOnly=true", ApiTestContext.Json, CancellationToken);

        Assert.NotEmpty(all!);
        Assert.Empty(agentsOnly!);
    }

    private async Task<string[]> KeysAsync(string filter)
    {
        var page = await Client.GetFromJsonAsync<PagedResult<WorkItemView>>(
            $"/api/v1/orgs/work-items/projects/{Project.Key}/items/?filter={Uri.EscapeDataString(filter)}",
            ApiTestContext.Json, CancellationToken);
        return [.. page!.Items.Select(x => x.Key)];
    }
}
