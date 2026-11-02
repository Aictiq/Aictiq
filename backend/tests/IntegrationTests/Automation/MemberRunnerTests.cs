using System.Net;
using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// A member's assigned runner (Settings → Members): only Admins set it, and every run a
/// non-Admin starts themselves - Start run, refine, chat - goes to it whatever they ask for.
/// </summary>
[Trait("Category", "Automation")]
public sealed class MemberRunnerTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    // An idle claim long-polls; a short poll keeps "nothing to claim" from costing 25 s a test.
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
        settings["Automation:PollTimeoutSeconds"] = "2";

    private string MemberRunners => $"{ProjectBase}/member-runners";

    private async Task<(HttpClient Client, string UserId)> MemberAsync(OrgRole role = OrgRole.Member)
    {
        var auth = await Context.RegisterAsync($"member-{Guid.NewGuid():N}@test.local");
        await AddMemberAsync(auth.User.Id, role, canOperateFactory: true);
        return (Context.ClientFor(auth), auth.User.Id);
    }

    private Task<HttpResponseMessage> AssignAsync(HttpClient client, string userId, Guid? runnerId) =>
        client.PutAsJsonAsync($"{MemberRunners}/{userId}", new SetMemberRunnerRequest(runnerId), ApiTestContext.Json, Ct);

    private async Task<RunView> StartRunAsync(HttpClient client, string itemKey, Guid? runnerId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, runnerId), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    [Fact]
    public async Task an_admin_sets_and_clears_a_members_runner_and_a_member_may_not()
    {
        var (member, memberId) = await MemberAsync();
        using var _ = member;

        var saved = await AssignAsync(Owner, memberId, Runner.Runner.Id);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var listed = Assert.Single((await Owner.GetFromJsonAsync<List<MemberRunnerView>>(MemberRunners, ApiTestContext.Json, Ct))!);
        Assert.Equal((memberId, Runner.Runner.Id, "box-1", true), (listed.UserId, listed.RunnerId, listed.RunnerName, listed.RunnerUsable));

        var mine = (await member.GetFromJsonAsync<MyRunnerView>($"{MemberRunners}/me", ApiTestContext.Json, Ct))!;
        Assert.Equal((Runner.Runner.Id, "box-1", true), (mine.RunnerId, mine.RunnerName, mine.Locked));

        // A member may neither read the assignments nor change anyone's, their own included.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(MemberRunners, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(member, memberId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(member, OwnerId, Runner.Runner.Id)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await AssignAsync(Owner, memberId, null)).StatusCode);
        Assert.Empty((await Owner.GetFromJsonAsync<List<MemberRunnerView>>(MemberRunners, ApiTestContext.Json, Ct))!);
        mine = (await member.GetFromJsonAsync<MyRunnerView>($"{MemberRunners}/me", ApiTestContext.Json, Ct))!;
        Assert.Null(mine.RunnerId);
    }

    [Fact]
    public async Task a_disabled_or_unknown_runner_or_a_non_member_is_refused()
    {
        var (member, memberId) = await MemberAsync();
        using var _ = member;
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignAsync(Owner, memberId, Guid.NewGuid())).StatusCode);

        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{Runner.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignAsync(Owner, memberId, Runner.Runner.Id)).StatusCode);

        var stranger = await Context.RegisterAsync($"stranger-{Guid.NewGuid():N}@test.local");
        var other = await RegisterRunnerAsync("box-2");
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(Owner, stranger.User.Id, other.Runner.Id)).StatusCode);
    }

    [Fact]
    public async Task a_members_start_run_goes_to_the_assigned_runner_whatever_it_names()
    {
        var other = await RegisterRunnerAsync("box-2");
        var (member, memberId) = await MemberAsync();
        using var _ = member;
        (await AssignAsync(Owner, memberId, Runner.Runner.Id)).EnsureSuccessStatusCode();

        // A crafted request naming box-2 still runs on box-1.
        var run = await StartRunAsync(member, ItemKey, other.Runner.Id);
        Assert.Equal(Runner.Runner.Id, run.RequestedRunnerId);

        using var otherClient = RunnerClient(other.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(otherClient))!.StatusCode);
        using var assignedClient = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(assignedClient)).RunId);
    }

    [Fact]
    public async Task an_admin_with_an_assigned_runner_may_still_pick_any()
    {
        var other = await RegisterRunnerAsync("box-2");
        var (admin, adminId) = await MemberAsync(OrgRole.Admin);
        using var _ = admin;
        (await AssignAsync(Owner, adminId, Runner.Runner.Id)).EnsureSuccessStatusCode();
        (await AssignAsync(Owner, OwnerId, Runner.Runner.Id)).EnsureSuccessStatusCode();

        var mine = (await admin.GetFromJsonAsync<MyRunnerView>($"{MemberRunners}/me", ApiTestContext.Json, Ct))!;
        Assert.False(mine.Locked);

        Assert.Equal(other.Runner.Id, (await StartRunAsync(admin, ItemKey, other.Runner.Id)).RequestedRunnerId);
        var second = await CreateItemAsync("Fix the logout redirect");
        Assert.Equal(other.Runner.Id, (await StartRunAsync(Owner, second.Key, other.Runner.Id)).RequestedRunnerId);
        var third = await CreateItemAsync("Fix the signup redirect");
        Assert.Null((await StartRunAsync(admin, third.Key, null)).RequestedRunnerId);
    }

    [Fact]
    public async Task members_without_a_runner_choose_as_before()
    {
        var other = await RegisterRunnerAsync("box-2");
        var (member, _) = await MemberAsync();
        using var client = member;

        Assert.Equal(other.Runner.Id, (await StartRunAsync(client, ItemKey, other.Runner.Id)).RequestedRunnerId);
        var second = await CreateItemAsync("Fix the logout redirect");
        Assert.Null((await StartRunAsync(client, second.Key, null)).RequestedRunnerId);
    }

    [Fact]
    public async Task a_members_refine_and_chat_go_to_the_assigned_runner()
    {
        var other = await RegisterRunnerAsync("box-2");
        var (member, memberId) = await MemberAsync();
        using var _ = member;
        (await Owner.PostAsync($"{ProjectBase}/refinement-settings/starter-playbook", null, Ct)).EnsureSuccessStatusCode();
        (await AssignAsync(Owner, memberId, Runner.Runner.Id)).EnsureSuccessStatusCode();

        var refined = await member.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/refinement/",
            new RefineItemRequest(RunnerId: other.Runner.Id), ApiTestContext.Json, Ct);
        Assert.True(refined.IsSuccessStatusCode, await refined.Content.ReadAsStringAsync(Ct));
        var refinement = (await refined.Content.ReadFromJsonAsync<RefinementView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(Runner.Runner.Id, (await RunAsync(refinement.LastRunId!.Value)).RequestedRunnerId);

        var asked = await member.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Where is the login redirect handled?" },
            ApiTestContext.Json, Ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, await asked.Content.ReadAsStringAsync(Ct));
        var chat = (await asked.Content.ReadFromJsonAsync<ChatView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(("box-1", true), (chat.RunnerName, chat.RunnerAssigned));
        Assert.Equal(Runner.Runner.Id, (await RunAsync(Assert.Single(chat.Messages).RunId!.Value)).RequestedRunnerId);
    }

    [Fact]
    public async Task a_disabled_or_deleted_assigned_runner_falls_back_to_the_default_or_any_free_runner()
    {
        var fallback = await RegisterRunnerAsync("box-2");
        var assigned = await RegisterRunnerAsync("box-3");
        var (member, memberId) = await MemberAsync();
        using var _ = member;
        (await AssignAsync(Owner, memberId, assigned.Runner.Id)).EnsureSuccessStatusCode();

        // Disabled: the member picks freely again, and their chat goes to the project's default.
        (await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{assigned.Runner.Id}",
            new UpdateRunnerRequest(null, true), ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        Assert.Null((await member.GetFromJsonAsync<MyRunnerView>($"{MemberRunners}/me", ApiTestContext.Json, Ct))!.RunnerId);
        Assert.Equal(Runner.Runner.Id, (await StartRunAsync(member, ItemKey, Runner.Runner.Id)).RequestedRunnerId);

        var current = (await Owner.GetFromJsonAsync<FactorySettingsView>($"{ProjectBase}/factory-settings", ApiTestContext.Json, Ct))!;
        (await Owner.PutAsJsonAsync($"{ProjectBase}/factory-settings",
            new UpdateFactorySettingsRequest(current.RepoSource, current.RepoFullName, current.DefaultBranch,
                current.LocalPathHint, current.DefaultAgentId, current.Version, fallback.Runner.Id),
            ApiTestContext.Json, Ct)).EnsureSuccessStatusCode();
        var asked = await member.PostAsJsonAsync($"{ProjectBase}/chat", new { question = "Where is the login redirect handled?" },
            ApiTestContext.Json, Ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, await asked.Content.ReadAsStringAsync(Ct));
        var chat = (await asked.Content.ReadFromJsonAsync<ChatView>(ApiTestContext.Json, Ct))!;
        Assert.Equal(("box-2", false), (chat.RunnerName, chat.RunnerAssigned));
        Assert.Equal(fallback.Runner.Id, (await RunAsync(Assert.Single(chat.Messages).RunId!.Value)).RequestedRunnerId);

        // Deleted: the assignment goes with it.
        (await Owner.DeleteAsync($"/api/v1/orgs/{Slug}/runners/{assigned.Runner.Id}", Ct)).EnsureSuccessStatusCode();
        Assert.Empty((await Owner.GetFromJsonAsync<List<MemberRunnerView>>(MemberRunners, ApiTestContext.Json, Ct))!);
        var second = await CreateItemAsync("Fix the logout redirect");
        Assert.Null((await StartRunAsync(member, second.Key, null)).RequestedRunnerId);
    }

    private sealed record ChatView(IReadOnlyList<ChatMessageView> Messages, bool RunnerOnline, string? RunnerName, bool RunnerAssigned);

    private sealed record ChatMessageView(Guid Id, string Status, string? RunStatus, Guid? RunId);
}
