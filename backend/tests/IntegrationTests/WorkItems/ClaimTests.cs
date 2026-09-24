using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// The claim compare-and-swap. It is one raw <c>UPDATE</c> whose <c>WHERE</c> carries the
/// expected version, so it cannot be exercised by unit-testing the handler: the predicate
/// lives in Postgres. Two agents racing for one item is the case that matters, and the
/// first version of this shipped comparing <c>xmin</c> to a <c>bigint</c>, which Postgres
/// has no operator for - every claim failed with a 500 and nothing noticed.
/// </summary>
[Trait("Category", "WorkItems")]
public sealed class ClaimTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task claiming_assigns_the_item_and_moves_it_to_an_active_state()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Claimable");

        var claimed = await ClaimAsync(item.Key, item.Version);

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var view = (await claimed.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;
        Assert.Equal(UserId, view.ClaimedBy);
        Assert.Equal(UserId, view.AssigneeId);
        Assert.NotNull(view.ClaimedAt);
        Assert.NotNull(view.ClaimHeartbeatAt);
        Assert.Equal(WorkflowStateCategory.Active, view.StateCategory);
    }

    [Fact]
    public async Task a_second_claim_is_a_conflict_that_names_the_current_holder()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Contested");
        var first = await ClaimAsync(item.Key, item.Version);
        first.EnsureSuccessStatusCode();
        var held = (await first.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, CancellationToken))!;

        var second = await ClaimAsync(item.Key, held.Version);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<Dictionary<string, object>>(ApiTestContext.Json, CancellationToken);
        Assert.Contains("already-claimed", problem!["type"].ToString());
        Assert.Contains(UserId, problem["claimedBy"].ToString());
    }

    [Fact]
    public async Task a_stale_version_loses_the_race_rather_than_overwriting_the_winner()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Raced");
        // Any write bumps xmin, so the version read a moment ago is exactly the token a
        // losing racer would still be holding.
        var renamed = await Client.PatchAsJsonAsync(ItemPath(item.Key),
            new UpdateWorkItemRequest(null, "Raced, renamed", null, null, null, null, null, null, null, null, null, null, null, null, item.Version),
            ApiTestContext.Json, CancellationToken);
        renamed.EnsureSuccessStatusCode();

        var stale = await ClaimAsync(item.Key, item.Version);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        Assert.Null(current!.ClaimedBy);
    }

    [Fact]
    public async Task releasing_clears_the_claim_and_heartbeat_renews_it()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Held");
        var claimed = await ClaimAsync(item.Key, item.Version);
        claimed.EnsureSuccessStatusCode();

        // Both answer 204: they act on a claim that already exists and have nothing to say.
        var beat = await Client.PostAsync(ItemPath(item.Key, "heartbeat"), null, CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, beat.StatusCode);
        var beaten = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        Assert.Equal(UserId, beaten!.ClaimedBy);

        var released = await Client.PostAsync(ItemPath(item.Key, "release"), null, CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        var free = (await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken))!;
        Assert.Null(free.ClaimedBy);
        Assert.Null(free.ClaimHeartbeatAt);

        // Released means claimable again, by the same version token the release returned.
        var again = await ClaimAsync(item.Key, free.Version);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    private Task<HttpResponseMessage> ClaimAsync(string key, uint version) =>
        Client.PostAsJsonAsync(ItemPath(key, "claim"), new ClaimRequest(version), ApiTestContext.Json, CancellationToken);
}
