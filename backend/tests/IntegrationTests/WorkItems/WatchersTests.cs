using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;

namespace Aictiq.IntegrationTests.WorkItems;

[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class WatchersTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task author_is_watching_and_an_explicit_unwatch_survives_a_later_comment()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Subscription rules");
        Assert.True(item.IsWatching);
        Assert.Equal(1, item.WatcherCount);

        var unwatch = await Client.DeleteAsync(ItemPath(item.Key, "watch"), CancellationToken);
        unwatch.EnsureSuccessStatusCode();
        var afterUnwatch = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        Assert.False(afterUnwatch!.IsWatching);
        Assert.Equal(0, afterUnwatch.WatcherCount);

        var comment = await Client.PostAsJsonAsync(ItemPath(item.Key, "comments"),
            new CreateCommentRequest("A later comment must not override my choice."), ApiTestContext.Json, CancellationToken);
        comment.EnsureSuccessStatusCode();

        var afterComment = await Client.GetFromJsonAsync<WorkItemView>(ItemPath(item.Key), ApiTestContext.Json, CancellationToken);
        Assert.False(afterComment!.IsWatching);
        Assert.Equal(0, afterComment.WatcherCount);

        var watch = await Client.PutAsync(ItemPath(item.Key, "watch"), null, CancellationToken);
        watch.EnsureSuccessStatusCode();
        var watchers = await Client.GetFromJsonAsync<List<ItemWatcherView>>(ItemPath(item.Key, "watch"), ApiTestContext.Json, CancellationToken);
        var watcher = Assert.Single(watchers!);
        Assert.Equal(ItemWatchReason.Manual, watcher.Reason);
    }
}
