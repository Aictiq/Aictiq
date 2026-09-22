using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Realtime;

/// <summary>
/// The realtime hub has no organization in its route, so it finds a project by key. Keys
/// are unique per organization, not per instance - these pin down that someone else's
/// project cannot take a key away from a team, nor a bound token reach past its binding.
/// </summary>
[Trait("Category", "Realtime")]
[Collection("postgres")]
public sealed class ProjectKeyResolutionTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _context = await ApiTestContext.CreateAsync(postgres, garage, "keyres");

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task a_same_key_project_in_another_organization_does_not_hide_yours()
    {
        var ada = await _context.RegisterAsync("ada@keyres.test", "Ada", "Lovelace");
        using var adaClient = _context.ClientFor(ada);
        await CreateOrganizationAsync(adaClient, "acme");
        var web = await CreateProjectAsync(adaClient, "acme", "WEB");

        var eve = await _context.RegisterAsync("eve@keyres.test", "Eve", "Else");
        using var eveClient = _context.ClientFor(eve);
        var evil = await CreateOrganizationAsync(eveClient, "evil");
        var evilWeb = await CreateProjectAsync(eveClient, "evil", "WEB");

        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<IProjectAccess>();

        Assert.Equal(web.Id, (await access.FindVisibleProjectAsync(ada.User.Id, "WEB", cancellationToken: CancellationToken))?.Id);
        Assert.Equal(evilWeb.Id, (await access.FindVisibleProjectAsync(eve.User.Id, "web", cancellationToken: CancellationToken))?.Id);
        // Narrowed to another organization, Ada's own visible WEB is not an answer: this is
        // how a token bound to one organization stays inside it.
        Assert.Null(await access.FindVisibleProjectAsync(ada.User.Id, "WEB", organizationId: evil.Id, cancellationToken: CancellationToken));
    }

    [Fact]
    public async Task two_visible_projects_with_one_key_need_the_organization_to_say_which()
    {
        var ada = await _context.RegisterAsync("grace@keyres.test", "Grace", "Hopper");
        using var client = _context.ClientFor(ada);
        await CreateOrganizationAsync(client, "north");
        await CreateOrganizationAsync(client, "south");
        await CreateProjectAsync(client, "north", "API");
        var south = await CreateProjectAsync(client, "south", "API");

        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<IProjectAccess>();

        Assert.Null(await access.FindVisibleProjectAsync(ada.User.Id, "API", cancellationToken: CancellationToken));
        Assert.Equal(south.Id, (await access.FindVisibleProjectAsync(ada.User.Id, "API", organizationSlug: "south", cancellationToken: CancellationToken))?.Id);
    }

    private async Task<OrganizationView> CreateOrganizationAsync(HttpClient client, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/v1/orgs", new CreateOrganizationRequest(slug, slug, null, null), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, CancellationToken))!;
    }

    private async Task<ProjectView> CreateProjectAsync(HttpClient client, string slug, string key)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{slug}/projects", new CreateProjectRequest($"Project {key}", key, null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, CancellationToken))!;
    }
}
