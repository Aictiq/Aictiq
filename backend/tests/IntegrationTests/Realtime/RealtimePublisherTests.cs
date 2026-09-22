using Aictiq.Api.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.WorkItems.Events;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Authorization;
using Aictiq.Modules.Notifications.Delivery;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Realtime;

[Trait("Category", "Realtime")]
public sealed class RealtimePublisherTests
{
    /// <summary>
    /// SignalR activates a hub through <c>ActivatorUtilities</c>, which refuses a type
    /// whose constructors are all satisfiable rather than choosing between them. A second
    /// constructor added "for tests" therefore breaks every real connection - the hub
    /// throws inside OnConnectedAsync and the browser sees only a closed socket, which no
    /// test that news up the hub directly can notice. This asserts the activation path.
    /// </summary>
    [Fact]
    public void hub_has_exactly_one_activatable_constructor()
    {
        var services = new ServiceCollection()
            .AddSingleton<IProjectAccess>(new HubAccess(null))
            .AddSingleton<INotificationPresence>(new NoPresence())
            .BuildServiceProvider();

        // CreateFactory, not CreateInstance: the factory is what SignalR's DefaultHubActivator
        // builds, and it is the one that refuses an ambiguous type. CreateInstance quietly
        // picks the greediest match and so cannot see this bug at all.
        var factory = ActivatorUtilities.CreateFactory(typeof(ProjectHub), Type.EmptyTypes);

        Assert.NotNull(factory(services, null));
    }

    [Fact]
    public async Task non_member_cannot_join_a_project_group()
    {
        var groups = new RecordingGroups();
        var hub = new ProjectHub(new HubAccess(null))
        {
            Context = new TestHubContext("ada"),
            Groups = groups,
        };

        var error = await Assert.ThrowsAsync<HubException>(() => hub.JoinProject("WEB"));

        Assert.Equal("Project not found.", error.Message);
        Assert.Empty(groups.Added);
    }

    [Fact]
    public async Task member_joins_only_its_resolved_project_group()
    {
        var projectId = Guid.CreateVersion7();
        var groups = new RecordingGroups();
        var hub = new ProjectHub(new HubAccess(new ProjectRef(projectId, "WEB", "Web", false)))
        {
            Context = new TestHubContext("ada"),
            Groups = groups,
        };

        await hub.JoinProject("WEB");

        Assert.Equal([("connection-1", ProjectHub.Group(projectId))], groups.Added);
    }

    [Fact]
    public async Task item_change_is_published_to_its_project_with_only_refetch_metadata()
    {
        var publisher = new RecordingPublisher();
        var projectId = Guid.CreateVersion7();
        var itemId = Guid.CreateVersion7();

        await new ItemChangedRealtimeHandler(publisher).HandleAsync(
            new ItemChanged(Guid.NewGuid(), projectId, itemId, "WEB-42", "ada", ["stateId"]), CancellationToken.None);

        Assert.Equal(projectId, publisher.ProjectId);
        Assert.Equal("item.changed", publisher.EventName);
        var payload = Assert.IsType<Dictionary<string, object>>(publisher.Payload);
        Assert.Equal(itemId, payload["id"]);
        Assert.Equal("WEB-42", payload["key"]);
        Assert.False(payload.ContainsKey("descriptionMarkdown"));
    }

    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public Guid ProjectId { get; private set; }
        public string? EventName { get; private set; }
        public object? Payload { get; private set; }

        public Task PublishAsync(Guid projectId, string eventName, object payload, CancellationToken cancellationToken = default)
        {
            ProjectId = projectId;
            EventName = eventName;
            Payload = payload.GetType().GetProperties().ToDictionary(property => property.Name,
                property => property.GetValue(payload)!);
            return Task.CompletedTask;
        }
    }

    private sealed class HubAccess(ProjectRef? project) : IProjectAccess
    {
        public Task<OrgRole?> GetOrgRoleAsync(string userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult<OrgRole?>(null);
        public Task<ProjectRef?> FindProjectAsync(Guid organizationId, string projectKey, CancellationToken cancellationToken = default) => Task.FromResult<ProjectRef?>(null);
        public Task<ProjectRef?> FindVisibleProjectAsync(string userId, string projectKey, Guid? organizationId = null, string? organizationSlug = null, CancellationToken cancellationToken = default) => Task.FromResult(project);
        public Task<ProjectRole?> GetProjectRoleAsync(string userId, Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectRole?>(null);
        public Task<IReadOnlyList<string>> ListProjectMemberIdsAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<Guid>> ListVisibleProjectIdsAsync(string userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class NoPresence : INotificationPresence
    {
        public Task SeenAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsActiveAsync(string userId, TimeSpan window, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class RecordingGroups : IGroupManager
    {
        public List<(string ConnectionId, string Group)> Added { get; } = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Added.Add((connectionId, groupName)); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHubContext(string userId) : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => userId;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
