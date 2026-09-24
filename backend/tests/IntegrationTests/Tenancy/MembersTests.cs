using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// Who is in an organization, who may change that, and what each role is allowed to see.
///
/// The memberships are inserted with SQL because there is no way to add someone yet -
/// invitations are tested separately. What is under test is what the endpoints do with a roster,
/// not how it came to exist.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class MembersTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private Guid _acmeId;
    private const string Acme = "acme";

    private readonly Dictionary<OrgRole, HttpClient> _clients = [];
    private readonly Dictionary<OrgRole, string> _ids = [];

    private HttpClient _outsider = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "members");

        // Alice creates the organization, so she is its Owner without any help.
        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _clients[OrgRole.Owner] = _context.ClientFor(alice);
        _ids[OrgRole.Owner] = alice.User.Id;

        var response = await _clients[OrgRole.Owner].PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        _acmeId = (await response.Content.ReadFromJsonAsync<OrganizationView>(
            ApiTestContext.Json, TestContext.Current.CancellationToken))!.Id;

        foreach (var (role, email, first, last) in new[]
        {
            (OrgRole.Admin, "bob@test.local", "Bob", "Brooks"),
            (OrgRole.Member, "carol@test.local", "Carol", "Carter"),
            (OrgRole.Guest, "dave@test.local", "Dave", "Doyle"),
        })
        {
            var auth = await _context.RegisterAsync(email, first, last);
            _clients[role] = _context.ClientFor(auth);
            _ids[role] = auth.User.Id;
            await AddMemberAsync(auth.User.Id, role);
        }

        _outsider = _context.ClientFor(await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
        _outsider.Dispose();
        await _context.DisposeAsync();
    }

    // ---------------------------------------------------------------- reading the roster

    [Fact]
    public async Task the_roster_lists_every_member_with_their_role()
    {
        var ct = TestContext.Current.CancellationToken;

        var page = await ListAsync(_clients[OrgRole.Owner], ct: ct);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(
            [OrgRole.Owner, OrgRole.Admin, OrgRole.Member, OrgRole.Guest],
            // Ordered by name: Alice, Bob, Carol, Dave - which is also role order here.
            page.Items.Select(m => m.Role));
        Assert.Equal("Alice Anderson", page.Items[0].DisplayName);
    }

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.Member)]
    [InlineData(OrgRole.Guest)]
    public async Task every_member_can_see_who_else_is_here(OrgRole role)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _clients[role].GetAsync($"/api/v1/orgs/{Acme}/members", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task an_outsider_cannot_tell_the_roster_from_an_organization_that_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;

        var hers = await _outsider.GetAsync($"/api/v1/orgs/{Acme}/members", ct);
        var imaginary = await _outsider.GetAsync("/api/v1/orgs/no-such-org/members", ct);

        Assert.Equal(HttpStatusCode.NotFound, hers.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, imaginary.StatusCode);
    }

    [Fact]
    public async Task a_guest_sees_who_is_here_but_not_the_addresses()
    {
        var ct = TestContext.Current.CancellationToken;

        var asMember = await ListAsync(_clients[OrgRole.Member], ct: ct);
        var asGuest = await ListAsync(_clients[OrgRole.Guest], ct: ct);

        Assert.All(asMember.Items, m => Assert.False(string.IsNullOrEmpty(m.Email)));
        // A guest is often a contractor or a customer: they can see the team without
        // leaving with its address book.
        Assert.All(asGuest.Items, m => Assert.Null(m.Email));
        Assert.Equal(asMember.TotalCount, asGuest.TotalCount);
    }

    [Fact]
    public async Task the_roster_can_be_searched_by_name_or_address()
    {
        var ct = TestContext.Current.CancellationToken;

        var byName = await ListAsync(_clients[OrgRole.Owner], search: "carol", ct: ct);
        var byEmail = await ListAsync(_clients[OrgRole.Owner], search: "bob@test", ct: ct);
        var nobody = await ListAsync(_clients[OrgRole.Owner], search: "zzz", ct: ct);

        Assert.Equal("Carol Carter", Assert.Single(byName.Items).DisplayName);
        Assert.Equal("Bob Brooks", Assert.Single(byEmail.Items).DisplayName);
        Assert.Empty(nobody.Items);
        Assert.Equal(0, nobody.TotalCount);
    }

    [Fact]
    public async Task paging_reports_the_whole_roster_not_just_the_page()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await ListAsync(_clients[OrgRole.Owner], page: 1, pageSize: 2, ct: ct);
        var second = await ListAsync(_clients[OrgRole.Owner], page: 2, pageSize: 2, ct: ct);

        Assert.Equal(4, first.TotalCount);
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        // A page order that is not total would repeat or skip people between pages.
        Assert.Empty(first.Items.Select(m => m.UserId).Intersect(second.Items.Select(m => m.UserId)));
    }

    [Fact]
    public async Task the_roster_never_reaches_across_organizations()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _outsider.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Initech", "initech", null, null), ct);
        response.EnsureSuccessStatusCode();

        var theirs = await ListAsync(_outsider, slug: "initech", ct: ct);

        Assert.Equal("Mallory Mahoney", Assert.Single(theirs.Items).DisplayName);
    }

    // ------------------------------------------------------------------- changing a role

    [Fact]
    public async Task an_owner_may_grant_and_revoke_owner()
    {
        var ct = TestContext.Current.CancellationToken;

        var granted = await SetRoleAsync(_clients[OrgRole.Owner], _ids[OrgRole.Member], OrgRole.Owner, ct);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.Equal(OrgRole.Owner,
            (await granted.Content.ReadFromJsonAsync<MembershipView>(ApiTestContext.Json, ct))!.Role);

        // And back down again - possible now only because Carol is not the last Owner.
        var revoked = await SetRoleAsync(_clients[OrgRole.Owner], _ids[OrgRole.Member], OrgRole.Member, ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
    }

    [Theory]
    [InlineData(OrgRole.Guest)]
    [InlineData(OrgRole.Member)]
    public async Task an_admin_manages_members_and_guests(OrgRole desired)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SetRoleAsync(_clients[OrgRole.Admin], _ids[OrgRole.Member], desired, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.Owner)]
    public async Task an_admin_cannot_promote_anyone_to_their_own_level_or_above(OrgRole desired)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SetRoleAsync(_clients[OrgRole.Admin], _ids[OrgRole.Member], desired, ct);

        // 403, not 404: Bob can see Carol, so refusing out loud tells him nothing new.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task an_admin_cannot_touch_another_admin_or_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await AddMemberAsync(await RegisterAsync("erin@test.local", "Erin", "Ellis"), OrgRole.Admin);

        var peer = await SetRoleAsync(_clients[OrgRole.Admin], _ids[OrgRole.Admin], OrgRole.Guest, ct);
        var above = await SetRoleAsync(_clients[OrgRole.Admin], _ids[OrgRole.Owner], OrgRole.Guest, ct);

        Assert.Equal(HttpStatusCode.Forbidden, peer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, above.StatusCode);
    }

    [Theory]
    [InlineData(OrgRole.Member)]
    [InlineData(OrgRole.Guest)]
    public async Task members_and_guests_cannot_change_roles_at_all(OrgRole actor)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SetRoleAsync(_clients[actor], _ids[OrgRole.Guest], OrgRole.Member, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task a_role_that_is_not_a_role_is_a_validation_error()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _clients[OrgRole.Owner].PutAsJsonAsync(
            $"/api/v1/orgs/{Acme}/members/{_ids[OrgRole.Member]}", new { role = "emperor" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task someone_who_is_not_a_member_is_indistinguishable_from_someone_who_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var mallory = await RegisterAsync("mallory2@test.local", "Mallory", "Mahoney");

        var stranger = await SetRoleAsync(_clients[OrgRole.Owner], mallory, OrgRole.Member, ct);
        var imaginary = await SetRoleAsync(_clients[OrgRole.Owner], "nobody-at-all", OrgRole.Member, ct);

        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, imaginary.StatusCode);
    }

    // ------------------------------------------------------------------------- removal

    [Fact]
    public async Task an_owner_can_remove_an_admin()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await RemoveAsync(_clients[OrgRole.Owner], _ids[OrgRole.Admin], ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(3, (await ListAsync(_clients[OrgRole.Owner], ct: ct)).TotalCount);
    }

    [Fact]
    public async Task an_admin_can_remove_a_guest_but_not_a_peer()
    {
        var ct = TestContext.Current.CancellationToken;
        var erin = await RegisterAsync("erin2@test.local", "Erin", "Ellis");
        await AddMemberAsync(erin, OrgRole.Admin);

        var guest = await RemoveAsync(_clients[OrgRole.Admin], _ids[OrgRole.Guest], ct);
        var peer = await RemoveAsync(_clients[OrgRole.Admin], erin, ct);

        Assert.Equal(HttpStatusCode.NoContent, guest.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, peer.StatusCode);
    }

    [Fact]
    public async Task a_member_cannot_remove_someone_else()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await RemoveAsync(_clients[OrgRole.Member], _ids[OrgRole.Guest], ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.Member)]
    [InlineData(OrgRole.Guest)]
    public async Task anyone_may_leave_an_organization_they_joined(OrgRole role)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await RemoveAsync(_clients[role], _ids[role], ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task the_last_owner_cannot_leave()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await RemoveAsync(_clients[OrgRole.Owner], _ids[OrgRole.Owner], ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/last-owner", problem!.Type);

        // And she is still there - the refusal rolled the whole write back.
        Assert.Equal(4, (await ListAsync(_clients[OrgRole.Owner], ct: ct)).TotalCount);
    }

    [Fact]
    public async Task the_last_owner_cannot_demote_themselves()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SetRoleAsync(_clients[OrgRole.Owner], _ids[OrgRole.Owner], OrgRole.Admin, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task an_owner_can_step_down_once_someone_else_owns_the_place()
    {
        var ct = TestContext.Current.CancellationToken;

        (await SetRoleAsync(_clients[OrgRole.Owner], _ids[OrgRole.Admin], OrgRole.Owner, ct))
            .EnsureSuccessStatusCode();
        var response = await RemoveAsync(_clients[OrgRole.Owner], _ids[OrgRole.Owner], ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task a_removed_member_loses_access_on_the_next_request()
    {
        var ct = TestContext.Current.CancellationToken;

        // Read something first, so their membership is in every cache it will ever be in.
        (await _clients[OrgRole.Guest].GetAsync($"/api/v1/orgs/{Acme}", ct)).EnsureSuccessStatusCode();

        (await RemoveAsync(_clients[OrgRole.Owner], _ids[OrgRole.Guest], ct)).EnsureSuccessStatusCode();

        // A cached role outliving the removal is the difference between revoked and not.
        var after = await _clients[OrgRole.Guest].GetAsync($"/api/v1/orgs/{Acme}", ct);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private Task<PagedResult<MemberView>> ListAsync(
        HttpClient client, string? slug = null, string? search = null, int page = 0, int pageSize = 0,
        CancellationToken ct = default)
    {
        List<string> query = [];
        if (page > 0) query.Add($"page={page}");
        if (pageSize > 0) query.Add($"pageSize={pageSize}");
        if (search is not null) query.Add($"search={Uri.EscapeDataString(search)}");

        var suffix = query.Count == 0 ? "" : $"?{string.Join('&', query)}";
        return client.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{slug ?? Acme}/members{suffix}", ApiTestContext.Json, ct)!;
    }

    private Task<HttpResponseMessage> SetRoleAsync(
        HttpClient client, string userId, OrgRole role, CancellationToken ct) =>
        client.PutAsJsonAsync($"/api/v1/orgs/{Acme}/members/{userId}",
            new UpdateMemberRequest(role), ApiTestContext.Json, ct);

    private Task<HttpResponseMessage> RemoveAsync(HttpClient client, string userId, CancellationToken ct) =>
        client.DeleteAsync($"/api/v1/orgs/{Acme}/members/{userId}", ct);

    private async Task<string> RegisterAsync(string email, string first, string last) =>
        (await _context.RegisterAsync(email, first, last)).User.Id;

    /// <summary>The only way to join before invitations exist.</summary>
    // ------------------------------------------------------ operating the factory

    [Fact]
    public async Task the_roster_says_who_operates_the_factory_and_the_role_decides_for_all_but_members()
    {
        var ct = TestContext.Current.CancellationToken;

        var roster = (await ListAsync(_clients[OrgRole.Owner], ct: ct)).Items;

        Assert.True(roster.Single(m => m.Role == OrgRole.Owner).CanOperateFactory);
        Assert.True(roster.Single(m => m.Role == OrgRole.Admin).CanOperateFactory);
        Assert.True(roster.Single(m => m.Role == OrgRole.Member).CanOperateFactory);
        Assert.False(roster.Single(m => m.Role == OrgRole.Guest).CanOperateFactory);
    }

    [Fact]
    public async Task an_admin_can_make_a_member_a_stakeholder_and_the_member_is_told_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var carol = _ids[OrgRole.Member];

        // Carol reads the organization first, so her answer is cached before it changes.
        Assert.True((await OrganizationAsync(_clients[OrgRole.Member], ct)).CanOperateFactory);

        var response = await PutAsync(_clients[OrgRole.Admin], carol, new UpdateMemberRequest(null, false), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipView>(ApiTestContext.Json, ct);
        Assert.Equal(OrgRole.Member, membership!.Role);
        Assert.False(membership.CanOperateFactory);
        Assert.False((await OrganizationAsync(_clients[OrgRole.Member], ct)).CanOperateFactory);

        // And back again: it is a flag, not a one-way door.
        await PutAsync(_clients[OrgRole.Owner], carol, new UpdateMemberRequest(null, true), ct);
        Assert.True((await OrganizationAsync(_clients[OrgRole.Member], ct)).CanOperateFactory);
    }

    [Theory]
    [InlineData(OrgRole.Admin, false)]
    [InlineData(OrgRole.Guest, true)]
    public async Task the_flag_cannot_contradict_a_role_that_decides_it(OrgRole target, bool desired)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PutAsync(_clients[OrgRole.Owner], _ids[target], new UpdateMemberRequest(null, desired), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task asking_for_what_the_role_already_decides_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PutAsync(_clients[OrgRole.Owner], _ids[OrgRole.Admin], new UpdateMemberRequest(null, true), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(OrgRole.Member)]
    [InlineData(OrgRole.Guest)]
    public async Task members_and_guests_cannot_decide_who_operates_the_factory(OrgRole actor)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PutAsync(_clients[actor], _ids[OrgRole.Member], new UpdateMemberRequest(null, false), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task demoting_to_guest_clears_the_flag_and_promoting_back_does_not_restore_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var carol = _ids[OrgRole.Member];

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(_clients[OrgRole.Owner], carol, new UpdateMemberRequest(OrgRole.Guest), ct)).StatusCode);
        var promoted = await PutAsync(_clients[OrgRole.Owner], carol, new UpdateMemberRequest(OrgRole.Member), ct);

        var membership = await promoted.Content.ReadFromJsonAsync<MembershipView>(ApiTestContext.Json, ct);
        Assert.Equal(OrgRole.Member, membership!.Role);
        Assert.False(membership.CanOperateFactory);
    }

    [Fact]
    public async Task the_database_refuses_a_guest_who_operates_the_factory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.organization_members SET can_operate_factory = true WHERE organization_id = @org AND user_id = @user",
            connection);
        command.Parameters.AddWithValue("org", _acmeId);
        command.Parameters.AddWithValue("user", _ids[OrgRole.Guest]);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_organization_members_guest_not_operator", error.ConstraintName);
    }

    private Task<HttpResponseMessage> PutAsync(HttpClient client, string userId, UpdateMemberRequest request, CancellationToken ct) =>
        client.PutAsJsonAsync($"/api/v1/orgs/{Acme}/members/{userId}", request, ApiTestContext.Json, ct);

    private static async Task<OrganizationView> OrganizationAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{Acme}", ApiTestContext.Json, ct))!;

    private async Task AddMemberAsync(string userId, OrgRole role)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @role <> 3)
            """, connection);
        command.Parameters.AddWithValue("org", _acmeId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (short)role);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
