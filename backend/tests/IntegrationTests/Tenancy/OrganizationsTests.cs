using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// The organization lifecycle over HTTP, and — the reason this ticket exists — the proof
/// that two customers on one database cannot see each other.
/// </summary>
[Trait("Category", "Tenancy")]
[Collection("postgres")]
public sealed class OrganizationsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _alice = null!;
    private HttpClient _mallory = null!;
    private string _malloryId = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "orgs");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        var mallory = await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney");

        _alice = _context.ClientFor(alice);
        _mallory = _context.ClientFor(mallory);
        _malloryId = mallory.User.Id;
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        _mallory.Dispose();
        await _context.DisposeAsync();
    }

    private static async Task<OrganizationView> CreateAsync(
        HttpClient client, string name, string? slug = null, CancellationToken ct = default)
    {
        var response = await client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(name, slug, null, null), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
    }

    [Fact]
    public async Task creating_an_organization_makes_the_creator_its_owner()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync(_alice, "Acme Corporation", ct: ct);

        Assert.Equal("acme-corporation", created.Slug);
        Assert.Equal(OrgRole.Owner, created.Role);
        Assert.Equal("UTC", created.TimeZone);
        Assert.Equal(DayOfWeek.Monday, created.WeekStart);

        var mine = await _alice.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);
        var membership = Assert.Single(mine!);
        Assert.Equal(created.Id, membership.Id);
        Assert.Equal(OrgRole.Owner, membership.Role);
    }

    [Fact]
    public async Task an_explicit_slug_is_honoured_and_a_taken_one_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync(_alice, "Acme", "acme-two", ct);
        Assert.Equal("acme-two", created.Slug);

        var clash = await _mallory.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Something else", "acme-two", null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        var problem = await clash.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("slug", problem!.Errors.Keys);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("settings")]
    [InlineData("admin")]
    public async Task a_slug_the_application_owns_is_reserved(string slug)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _alice.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Reserved", slug, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("slug", problem!.Errors.Keys);
    }

    [Theory]
    [InlineData("A")]                 // too short
    [InlineData("Has Spaces")]
    [InlineData("-leading")]
    [InlineData("double--hyphen")]
    [InlineData("what?")]
    public async Task a_malformed_slug_is_refused_before_it_reaches_the_database(string slug)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _alice.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Whatever", slug, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_slug_typed_in_capitals_is_normalised_rather_than_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync(_alice, "Acme", "ACME-Corp", ct);

        Assert.Equal("acme-corp", created.Slug);
    }

    [Fact]
    public async Task a_derived_slug_that_is_already_taken_gets_a_suffix_rather_than_an_error()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await CreateAsync(_alice, "Acme", ct: ct);
        var second = await CreateAsync(_mallory, "Acme", ct: ct);

        Assert.Equal("acme", first.Slug);
        Assert.Equal("acme-2", second.Slug);
    }

    [Fact]
    public async Task a_name_with_nothing_sluggable_in_it_still_produces_an_address()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync(_alice, "🚀", ct: ct);

        Assert.Equal("workspace", created.Slug);
    }

    [Fact]
    public async Task accents_are_folded_rather_than_dropped()
    {
        var ct = TestContext.Current.CancellationToken;

        var created = await CreateAsync(_alice, "Ćuljak Software", ct: ct);

        Assert.Equal("culjak-software", created.Slug);
    }

    [Fact]
    public async Task a_name_is_required()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _alice.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("   ", null, null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("name", problem!.Errors.Keys);
    }

    [Fact]
    public async Task an_unknown_time_zone_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _alice.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Timezoned", null, "Mars/Olympus_Mons", null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ct);
        Assert.Contains("timeZone", problem!.Errors.Keys);
    }

    [Fact]
    public async Task an_outsider_cannot_tell_a_private_organization_from_one_that_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        var hers = await _mallory.GetAsync($"/api/v1/orgs/{acme.Slug}", ct);
        var imaginary = await _mallory.GetAsync("/api/v1/orgs/no-such-org", ct);

        // Identical answers, deliberately: a 403 on the first would confirm it exists.
        Assert.Equal(HttpStatusCode.NotFound, hers.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, imaginary.StatusCode);

        var mine = await _mallory.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);
        Assert.Empty(mine!);
    }

    [Fact]
    public async Task renaming_keeps_the_address_stable()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        var response = await _alice.PatchAsJsonAsync($"/api/v1/orgs/{acme.Slug}",
            new UpdateOrganizationRequest("Acme Holdings", null, null, null, acme.Version), ct);

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;

        Assert.Equal("Acme Holdings", updated.Name);
        // Every bookmark, pasted link and agent config would break otherwise.
        Assert.Equal("acme", updated.Slug);
    }

    [Fact]
    public async Task settings_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        var response = await _alice.PatchAsJsonAsync($"/api/v1/orgs/{acme.Slug}",
            new UpdateOrganizationRequest(null, "Europe/Sarajevo", DayOfWeek.Sunday, null, acme.Version), ct);
        response.EnsureSuccessStatusCode();

        var reread = await _alice.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{acme.Slug}", ApiTestContext.Json, ct);

        Assert.Equal("Europe/Sarajevo", reread!.TimeZone);
        Assert.Equal(DayOfWeek.Sunday, reread.WeekStart);
        Assert.Equal("Acme", reread.Name);
    }

    [Fact]
    public async Task a_stale_version_loses_to_whoever_saved_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        var first = await _alice.PatchAsJsonAsync($"/api/v1/orgs/{acme.Slug}",
            new UpdateOrganizationRequest("First", null, null, null, acme.Version), ct);
        first.EnsureSuccessStatusCode();

        var second = await _alice.PatchAsJsonAsync($"/api/v1/orgs/{acme.Slug}",
            new UpdateOrganizationRequest("Second", null, null, null, acme.Version), ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task deleting_needs_the_owner_role_and_the_name_typed_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        var wrongName = await _alice.SendAsync(Delete(acme.Slug, "acme corp"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrongName.StatusCode);

        var stranger = await _mallory.SendAsync(Delete(acme.Slug, "Acme"), ct);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);

        var owner = await _alice.SendAsync(Delete(acme.Slug, "acme"), ct);
        Assert.Equal(HttpStatusCode.NoContent, owner.StatusCode);
    }

    [Fact]
    public async Task a_deleted_organization_disappears_from_every_route_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        (await _alice.SendAsync(Delete(acme.Slug, "Acme"), ct)).EnsureSuccessStatusCode();

        // Its owner included: soft delete hides it, it does not merely hide the button.
        var detail = await _alice.GetAsync($"/api/v1/orgs/{acme.Slug}", ct);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var mine = await _alice.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);
        Assert.Empty(mine!);
    }

    [Fact]
    public async Task a_deleted_organization_keeps_its_address_so_the_link_stays_dead()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);
        (await _alice.SendAsync(Delete(acme.Slug, "Acme"), ct)).EnsureSuccessStatusCode();

        // Otherwise someone else claims "acme" and inherits every stale bookmark.
        var response = await _mallory.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", "acme", null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task a_member_whose_role_is_too_low_gets_403_not_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await CreateAsync(_alice, "Acme", ct: ct);

        // Membership management is tested elsewhere, so the row goes in the way the database sees
        // it. What is under test is the filter's answer, not how the row got there.
        await AddMemberAsync(acme.Id, _malloryId, OrgRole.Admin, ct);

        // An Admin may rename it...
        var rename = await _mallory.PatchAsJsonAsync($"/api/v1/orgs/{acme.Slug}",
            new UpdateOrganizationRequest("Acme Ltd", null, null, null, acme.Version), ct);
        rename.EnsureSuccessStatusCode();

        // ...but deleting is the Owner's alone, and now that she can see the
        // organization, saying so out loud reveals nothing she did not already know.
        var delete = await _mallory.SendAsync(Delete(acme.Slug, "Acme Ltd"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task an_anonymous_visitor_gets_401_not_404()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = _context.Anonymous();

        var response = await anonymous.GetAsync("/api/v1/orgs", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task AddMemberAsync(Guid organizationId, string userId, OrgRole role, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static HttpRequestMessage Delete(string slug, string confirmation) =>
        new(HttpMethod.Delete, $"/api/v1/orgs/{slug}")
        {
            Content = JsonContent.Create(new DeleteOrganizationRequest(confirmation)),
        };
}
