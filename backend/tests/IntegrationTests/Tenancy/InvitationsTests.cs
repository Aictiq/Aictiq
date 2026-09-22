using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Paging;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// The only way into an organization other than creating one.
///
/// The instance under test has <b>no SMTP relay</b> - which is the deployment worth
/// defaulting to, because it is the one where the whole flow has to work on the link
/// alone. The queued mail is the email tests' business; what matters here is
/// that the API returns a usable link either way and says which case the caller is in.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class InvitationsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private const string Acme = "acme";

    private HttpClient _owner = null!;
    private HttpClient _admin = null!;
    private HttpClient _member = null!;
    private HttpClient _outsider = null!;
    private string _outsiderId = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "invitations");

        var alice = await _context.RegisterAsync("alice@test.local", "Alice", "Anderson");
        _owner = _context.ClientFor(alice);

        var created = await _owner.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Acme", Acme, null, null), TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();

        _admin = await JoinAsync("bob@test.local", "Bob", "Brooks", OrgRole.Admin);
        _member = await JoinAsync("carol@test.local", "Carol", "Carter", OrgRole.Member);

        var mallory = await _context.RegisterAsync("mallory@test.local", "Mallory", "Mahoney");
        _outsider = _context.ClientFor(mallory);
        _outsiderId = mallory.User.Id;
    }

    public async ValueTask DisposeAsync()
    {
        _owner.Dispose();
        _admin.Dispose();
        _member.Dispose();
        _outsider.Dispose();
        await _context.DisposeAsync();
    }

    // ------------------------------------------------------------------------- inviting

    [Fact]
    public async Task an_invitation_comes_back_with_a_link_even_when_no_relay_is_configured()
    {
        var ct = TestContext.Current.CancellationToken;

        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        // The link is the product on an instance that cannot send email, so it must be
        // whole: absolute, and carrying the token the accept page will present.
        Assert.False(link.EmailSent);
        Assert.Contains($"/invite/{link.Token}", link.AcceptUrl, StringComparison.Ordinal);
        Assert.StartsWith("http", link.AcceptUrl, StringComparison.Ordinal);
        Assert.Equal("dana@test.local", link.Invitation.Email);
        Assert.Equal(OrgRole.Member, link.Invitation.Role);
        Assert.Equal(InvitationStatus.Pending, link.Invitation.Status);
        Assert.Equal("Alice Anderson", link.Invitation.InvitedByName);
    }

    [Fact]
    public async Task the_address_is_stored_lower_cased_however_it_was_typed()
    {
        var ct = TestContext.Current.CancellationToken;

        var link = await InviteAsync(_owner, "  Dana.Dell@Test.Local ", OrgRole.Member, ct);
        // …and the same address in another casing is then a duplicate, not a second invite.
        var again = await PostInviteAsync(_owner, "DANA.DELL@test.local", OrgRole.Member, ct);

        Assert.Equal("dana.dell@test.local", link.Invitation.Email);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("two@at@example.com")]
    [InlineData("no@domain")]
    public async Task a_typo_is_refused_before_it_becomes_a_dead_letter(string address)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostInviteAsync(_owner, address, OrgRole.Member, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task owner_is_not_an_invitable_role()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostInviteAsync(_owner, "dana@test.local", OrgRole.Owner, ct);

        // Ownership is handed over from the members list, to someone already here, where
        // the last-owner trigger can see it happen.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_admin_invites_below_themselves_and_an_owner_invites_admins()
    {
        var ct = TestContext.Current.CancellationToken;

        var byAdmin = await PostInviteAsync(_admin, "erin@test.local", OrgRole.Admin, ct);
        var byOwner = await PostInviteAsync(_owner, "frank@test.local", OrgRole.Admin, ct);
        var lower = await PostInviteAsync(_admin, "gina@test.local", OrgRole.Guest, ct);

        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Created, lower.StatusCode);
    }

    [Fact]
    public async Task a_member_cannot_invite_and_an_outsider_cannot_tell_the_organization_exists()
    {
        var ct = TestContext.Current.CancellationToken;

        var asMember = await PostInviteAsync(_member, "dana@test.local", OrgRole.Guest, ct);
        var asStranger = await PostInviteAsync(_outsider, "dana@test.local", OrgRole.Guest, ct);

        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
    }

    [Fact]
    public async Task someone_already_here_is_not_invited_again()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PostInviteAsync(_owner, "carol@test.local", OrgRole.Member, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task naming_a_project_that_is_not_in_this_organization_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("dana@test.local", OrgRole.Member, Guid.NewGuid(), ProjectRole.Member),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ stakeholders

    /// <summary>
    /// The case the flag exists for: a client asked to follow one private project. They land
    /// on it as a Member, so they can see the board and add items, and the organization says
    /// they may not operate the factory, from the very first request.
    /// </summary>
    [Fact]
    public async Task a_stakeholder_joins_a_private_project_as_a_member_who_cannot_operate_the_factory()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreatePrivateProjectAsync("CLIENT", ct);

        var response = await _admin.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("client@test.local", OrgRole.Member, project.Id, ProjectRole.Member, false),
            ApiTestContext.Json, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;
        Assert.Equal("CLIENT", link.Invitation.ProjectKey);
        Assert.False(link.Invitation.CanOperateFactory);

        var client = _context.ClientFor(await _context.RegisterAsync("client@test.local", "Chris", "Client"));
        var accepted = await AcceptAsync(client, link.Token, ct);
        Assert.Equal("CLIENT", accepted.ProjectKey);

        var organization = await client.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{Acme}", ApiTestContext.Json, ct);
        Assert.Equal(OrgRole.Member, organization!.Role);
        Assert.False(organization.CanOperateFactory);

        // Private, and still visible: the invitation is what put them on it.
        var seen = await client.GetFromJsonAsync<ProjectView>($"/api/v1/orgs/{Acme}/projects/CLIENT", ApiTestContext.Json, ct);
        Assert.Equal(ProjectRole.Member, seen!.Role);
    }

    [Fact]
    public async Task a_member_invitation_operates_the_factory_by_default_and_a_guest_one_never_does()
    {
        var ct = TestContext.Current.CancellationToken;

        var member = await InviteAsync(_owner, "mia@test.local", OrgRole.Member, ct);
        var guest = await InviteAsync(_owner, "gus@test.local", OrgRole.Guest, ct);

        Assert.True(member.Invitation.CanOperateFactory);
        Assert.False(guest.Invitation.CanOperateFactory);
    }

    [Theory]
    [InlineData(OrgRole.Guest, true, null)]
    [InlineData(OrgRole.Admin, false, null)]
    [InlineData(OrgRole.Guest, null, ProjectRole.Member)]
    public async Task an_invitation_cannot_promise_what_the_role_does_not_allow(
        OrgRole role, bool? canOperateFactory, ProjectRole? projectRole)
    {
        var ct = TestContext.Current.CancellationToken;
        var project = projectRole is null ? null : await CreatePrivateProjectAsync($"P{(int)role}{Random.Shared.Next(100, 999)}", ct);

        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("ivy@test.local", role, project?.Id, projectRole, canOperateFactory),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_existing_guest_accepting_a_project_invitation_is_stored_as_a_project_guest()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreatePrivateProjectAsync("GUESTED", ct);
        var guest = await JoinAsync("gwen@test.local", "Gwen", "Guest", OrgRole.Guest);
        // A member invitation to another address, forwarded to someone who is already a Guest.
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("forwarded@test.local", OrgRole.Member, project.Id, ProjectRole.Admin),
            ApiTestContext.Json, ct);
        var link = (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;

        var accepted = await AcceptAsync(guest, link.Token, ct);

        Assert.Equal(OrgRole.Guest, accepted.Role);
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT role FROM tenancy.project_members WHERE project_id = @project AND user_id = (SELECT accepted_by FROM tenancy.invitations WHERE id = @invitation)",
            connection);
        command.Parameters.AddWithValue("project", project.Id);
        command.Parameters.AddWithValue("invitation", link.Invitation.Id);
        Assert.Equal((short)ProjectRole.Guest, (short)(await command.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task a_project_without_a_role_on_it_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreatePrivateProjectAsync("HALF", ct);

        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("hal@test.local", OrgRole.Member, project.Id, null),
            ApiTestContext.Json, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task accepting_as_someone_already_here_adds_the_project_but_leaves_the_factory_flag_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var project = await CreatePrivateProjectAsync("EXTRA", ct);
        // Carol is already a Member who operates the factory. A stakeholder link must not
        // quietly take that away, any more than a guest link demotes her.
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest("other@test.local", OrgRole.Member, project.Id, ProjectRole.Member, false),
            ApiTestContext.Json, ct);
        var link = (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;

        var accepted = await AcceptAsync(_member, link.Token, ct);

        Assert.True(accepted.AlreadyMember);
        var organization = await _member.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{Acme}", ApiTestContext.Json, ct);
        Assert.True(organization!.CanOperateFactory);
        var seen = await _member.GetFromJsonAsync<ProjectView>($"/api/v1/orgs/{Acme}/projects/EXTRA", ApiTestContext.Json, ct);
        Assert.Equal(ProjectRole.Member, seen!.Role);
    }

    // -------------------------------------------------------------------- the open list

    [Fact]
    public async Task the_list_shows_open_invitations_and_forgets_the_closed_ones()
    {
        var ct = TestContext.Current.CancellationToken;

        var kept = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);
        var revoked = await InviteAsync(_owner, "erin@test.local", OrgRole.Guest, ct);
        (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/invitations/{revoked.Invitation.Id}", ct))
            .EnsureSuccessStatusCode();

        var open = await ListAsync(_owner, ct);

        Assert.Equal(kept.Invitation.Id, Assert.Single(open).Id);
        // Never the token: it exists once, in the response that minted it.
        Assert.DoesNotContain("token", await (await _owner.GetAsync(
            $"/api/v1/orgs/{Acme}/invitations", ct)).Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task a_member_cannot_read_the_pending_list()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _member.GetAsync($"/api/v1/orgs/{Acme}/invitations", ct);

        // The pending list is a list of email addresses that have not agreed to anything.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task resending_mints_a_new_link_and_retires_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        var response = await _owner.PostAsync(
            $"/api/v1/orgs/{Acme}/invitations/{first.Invitation.Id}/resend", null, ct);
        var second = (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;

        Assert.NotEqual(first.Token, second.Token);
        // Only the hash was ever stored, so the same link cannot be sent twice - and the
        // usual reason to resend is that the first one went somewhere it should not have.
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(first.Token, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(second.Token, ct)).StatusCode);
        Assert.True(second.Invitation.ExpiresAt >= first.Invitation.ExpiresAt);
    }

    [Fact]
    public async Task a_revoked_address_can_be_invited_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/invitations/{first.Invitation.Id}", ct))
            .EnsureSuccessStatusCode();
        var second = await PostInviteAsync(_owner, "dana@test.local", OrgRole.Guest, ct);

        // The partial unique index closes the revoked row out of the way; the row itself
        // stays as the record that someone was asked once.
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    // -------------------------------------------------------------------------- preview

    [Fact]
    public async Task the_preview_names_the_organization_without_handing_out_the_address()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        using var anonymous = _context.Anonymous();
        var preview = (await anonymous.GetFromJsonAsync<InvitationPreview>(
            $"/api/v1/invitations/{link.Token}", ApiTestContext.Json, ct))!;

        Assert.Equal("Acme", preview.OrganizationName);
        Assert.Equal("Alice Anderson", preview.InvitedByName);
        Assert.Equal(OrgRole.Member, preview.Role);
        Assert.Equal(InvitationStatus.Pending, preview.Status);
        // Whoever opened the link is not necessarily who it was sent to.
        Assert.DoesNotContain("dana", preview.MaskedEmail, StringComparison.Ordinal);
        Assert.EndsWith("@test.local", preview.MaskedEmail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task an_invented_token_is_a_404_and_says_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await PreviewAsync("not-a-real-token", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------ accepting

    [Fact]
    public async Task accepting_joins_the_organization_at_the_invited_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "mallory@test.local", OrgRole.Guest, ct);

        var accepted = await AcceptAsync(_outsider, link.Token, ct);

        Assert.Equal(Acme, accepted.OrganizationSlug);
        Assert.Equal(OrgRole.Guest, accepted.Role);
        Assert.False(accepted.AlreadyMember);

        // And the membership is real on the very next request - a cached "not a member"
        // outliving the join is the same bug as a cached role outliving a removal.
        var roster = await _owner.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{Acme}/members", ApiTestContext.Json, ct);
        Assert.Contains(roster!.Items, m => m.UserId == _outsiderId && m.Role == OrgRole.Guest);
    }

    [Fact]
    public async Task a_link_forwarded_to_another_address_still_works_and_records_who_used_it()
    {
        var ct = TestContext.Current.CancellationToken;
        // Invited dana@, accepted by mallory@ - people forward mail, and refusing would
        // strand them with no way to join.
        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        var accepted = await AcceptAsync(_outsider, link.Token, ct);

        Assert.Equal(OrgRole.Member, accepted.Role);
        var (email, acceptedBy) = await ReadAcceptanceAsync(link.Invitation.Id, ct);
        Assert.Equal("dana@test.local", email);
        Assert.Equal(_outsiderId, acceptedBy);
    }

    [Fact]
    public async Task an_anonymous_visitor_is_asked_to_sign_in_rather_than_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        using var anonymous = _context.Anonymous();
        var response = await anonymous.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct);

        // 401, not 404: the client's next move is the login page with the link preserved.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task accepting_twice_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "mallory@test.local", OrgRole.Guest, ct);

        await AcceptAsync(_outsider, link.Token, ct);
        var again = await _outsider.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<ProblemDetails>(ct);
        Assert.Equal("https://aictiq.com/problems/conflict", problem!.Type);
    }

    [Fact]
    public async Task two_people_clicking_the_same_link_at_once_produce_exactly_one_join()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Member, ct);

        var erin = _context.ClientFor(await _context.RegisterAsync("erin@test.local", "Erin", "Ellis"));
        using var _ = erin;

        // A read-then-write accept lets both through; the conditional UPDATE is what makes
        // one of them lose, and neither transaction can see the other's uncommitted work.
        var responses = await Task.WhenAll(
            _outsider.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct),
            erin.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var roster = await _owner.GetFromJsonAsync<PagedResult<MemberView>>(
            $"/api/v1/orgs/{Acme}/members", ApiTestContext.Json, ct);
        Assert.Equal(4, roster!.TotalCount);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task a_revoked_invitation_cannot_be_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "mallory@test.local", OrgRole.Member, ct);

        (await _owner.DeleteAsync($"/api/v1/orgs/{Acme}/invitations/{link.Invitation.Id}", ct))
            .EnsureSuccessStatusCode();
        var response = await _outsider.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(await ListAsync(_owner, ct), i => i.Id == link.Invitation.Id);
    }

    [Fact]
    public async Task an_expired_invitation_cannot_be_accepted_but_can_still_be_seen()
    {
        var ct = TestContext.Current.CancellationToken;
        var link = await InviteAsync(_owner, "mallory@test.local", OrgRole.Member, ct);
        await ExpireAsync(link.Invitation.Id, ct);

        var response = await _outsider.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct);
        var preview = (await (await PreviewAsync(link.Token, ct))
            .Content.ReadFromJsonAsync<InvitationPreview>(ApiTestContext.Json, ct))!;
        var open = await ListAsync(_owner, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(InvitationStatus.Expired, preview.Status);
        // Still listed, and marked: a link that silently vanished on its seventh day looks
        // like a bug from the inviter's side, and resending is the obvious next move.
        Assert.Equal(InvitationStatus.Expired, Assert.Single(open).Status);
    }

    [Fact]
    public async Task an_expired_invitation_can_be_resent_and_then_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await InviteAsync(_owner, "mallory@test.local", OrgRole.Member, ct);
        await ExpireAsync(first.Invitation.Id, ct);

        var resent = (await (await _owner.PostAsync(
            $"/api/v1/orgs/{Acme}/invitations/{first.Invitation.Id}/resend", null, ct))
            .Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;
        var accepted = await AcceptAsync(_outsider, resent.Token, ct);

        Assert.Equal(OrgRole.Member, accepted.Role);
    }

    [Fact]
    public async Task accepting_an_invitation_you_did_not_need_does_not_change_your_role()
    {
        var ct = TestContext.Current.CancellationToken;
        // Carol is already a Member. An old "guest" link must not demote her.
        var link = await InviteAsync(_owner, "dana@test.local", OrgRole.Guest, ct);

        var accepted = await AcceptAsync(_member, link.Token, ct);

        Assert.True(accepted.AlreadyMember);
        Assert.Equal(OrgRole.Member, accepted.Role);
    }

    // --------------------------------------------------------------------------- helpers

    private async Task<HttpClient> JoinAsync(string email, string first, string last, OrgRole role)
    {
        var ct = TestContext.Current.CancellationToken;
        var auth = await _context.RegisterAsync(email, first, last);
        var client = _context.ClientFor(auth);

        var link = await InviteAsync(_owner, email, role, ct);
        (await client.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private Task<HttpResponseMessage> PostInviteAsync(
        HttpClient client, string email, OrgRole role, CancellationToken ct) =>
        client.PostAsJsonAsync($"/api/v1/orgs/{Acme}/invitations",
            new CreateInvitationRequest(email, role, null, null), ApiTestContext.Json, ct);

    private async Task<InvitationLink> InviteAsync(
        HttpClient client, string email, OrgRole role, CancellationToken ct)
    {
        var response = await PostInviteAsync(client, email, role, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, ct))!;
    }

    private async Task<AcceptedInvitation> AcceptAsync(HttpClient client, string token, CancellationToken ct)
    {
        var response = await client.PostAsync($"/api/v1/invitations/{token}/accept", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AcceptedInvitation>(ApiTestContext.Json, ct))!;
    }

    private async Task<ProjectView> CreatePrivateProjectAsync(string key, CancellationToken ct)
    {
        var response = await _owner.PostAsJsonAsync($"/api/v1/orgs/{Acme}/projects",
            new CreateProjectRequest($"Project {key}", key, null, ProjectVisibility.Private, null, null),
            ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
    }

    /// <summary>The preview is the one endpoint here with no principal at all.</summary>
    private async Task<HttpResponseMessage> PreviewAsync(string token, CancellationToken ct)
    {
        using var anonymous = _context.Anonymous();
        var response = await anonymous.GetAsync($"/api/v1/invitations/{token}", ct);
        // Buffered before the client goes out of scope: the test-host response body is a
        // stream owned by the handler, and reading it afterwards reads a disposed one.
        await response.Content.LoadIntoBufferAsync(ct);
        return response;
    }

    private async Task<IReadOnlyList<InvitationView>> ListAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<InvitationView>>(
            $"/api/v1/orgs/{Acme}/invitations", ApiTestContext.Json, ct))!;

    /// <summary>Backdating past the expiry is the only way to age a row in a test.</summary>
    private async Task ExpireAsync(Guid invitationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.invitations SET expires_at = now() - interval '1 hour' WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", invitationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Read with SQL because the API never exposes it: the address an invitation was sent
    /// to, beside the account that used it, is the record that the two differed.
    /// </summary>
    private async Task<(string Email, string? AcceptedBy)> ReadAcceptanceAsync(
        Guid invitationId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT email, accepted_by FROM tenancy.invitations WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", invitationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}
