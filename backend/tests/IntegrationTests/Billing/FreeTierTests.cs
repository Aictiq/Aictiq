using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Billing.Domain;
using Aictiq.Modules.Billing.Endpoints;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// The hosted free tier: what an evaluation ends in when <c>Billing:FreeTier:Enabled</c> is
/// on, and the limits that belong to a person rather than an organization - three people and
/// a pooled attachment allowance across every unpaid organization an Owner owns.
///
/// Every test builds its own owners and organizations, so the counting is never polluted by
/// a neighbour's: the per-Owner footprint is exactly what is under test.
/// </summary>
[Trait("Category", "Billing")]
public sealed class FreeTierTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    /// <summary>One file of <see cref="FileBytes"/> fits the pooled allowance; two do not.</summary>
    private const long PooledBytes = 6_000;
    private const int FileBytes = 4_096;

    private BillingTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await BillingTestHost.CreateAsync(postgres, garage, "free_tier", configure: settings =>
        {
            settings["Billing:FreeTier:Enabled"] = "true";
            // The pool is narrowed to a few kilobytes so two uploads, not 200 MiB, cross it.
            settings["Billing:FreeTier:StorageBytes"] = PooledBytes.ToString();
            settings["RateLimiting:UploadPermitLimitPerMinute"] = "1000";
        });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // ------------------------------------------------------------- evaluation to Free

    [Fact]
    public async Task an_evaluation_that_ends_within_the_limits_drops_to_hosted_free_and_keeps_working()
    {
        var owner = await OwnerAsync("ends");
        var org = await OrganizationAsync(owner, "ends-co");
        await JoinAsync(org, await PersonAsync("ends-mate"));

        Assert.Equal(PlanCodes.Hosted, (await SubscriptionAsync(org)).Plan);
        await ExpireAsync(org);

        var subscription = await SubscriptionAsync(org);
        Assert.Equal(PlanCodes.HostedFree, subscription.Plan);
        Assert.False(subscription.ReadOnly);
        Assert.Null(subscription.ReadOnlyReason);
        Assert.True(subscription.Evaluation!.Expired);
        var freeTier = Assert.IsType<FreeTierView>(subscription.FreeTier);
        Assert.Equal(3, freeTier.MaxPeople);
        Assert.Equal(2, freeTier.People);
        Assert.Equal(PooledBytes, freeTier.StorageBytes);
        Assert.Equal(2, freeTier.MaxRunners);
        Assert.Equal(30, freeTier.RunLogDays);

        // The plan list offers Hosted at the new price, and the free tier rather than the legacy Free.
        Assert.Equal(79m, Assert.Single(subscription.Plans, p => p.Code == PlanCodes.Hosted).OrganizationPrice);
        Assert.Contains(subscription.Plans, p => p.Code == PlanCodes.HostedFree);
        Assert.DoesNotContain(subscription.Plans, p => p.Code == PlanCodes.Free);

        // Writes keep working; everything else matches Hosted.
        await CreateProjectAsync(org, "ONE");
        await CreateProjectAsync(org, "TWO");
        await CreateProjectAsync(org, "THR");
        await CreateProjectAsync(org, "FOU");

        using var scope = _host.Context.Factory.Services.CreateScope();
        var allowances = scope.ServiceProvider.GetRequiredService<IPlanAllowances>();
        Assert.Equal(30, await allowances.GetRunLogRetentionDaysAsync(org.Id, Ct));
        Assert.Equal(365, await allowances.GetAnalyticsHistoryDaysAsync(org.Id, Ct));
    }

    [Fact]
    public async Task an_owner_over_the_people_limit_when_the_evaluation_ends_is_read_only_until_back_under_or_paid()
    {
        var owner = await OwnerAsync("over");
        var org = await OrganizationAsync(owner, "over-co");
        var project = await CreateProjectAsync(org, "WEB");
        // During the evaluation the organization is Hosted, so nothing stops a fourth person.
        var first = await InviteAsync(org, "over-1@test.local");
        await InviteAsync(org, "over-2@test.local");
        await InviteAsync(org, "over-3@test.local");

        await ExpireAsync(org);

        var subscription = await SubscriptionAsync(org);
        Assert.Equal(PlanCodes.HostedFree, subscription.Plan);
        Assert.True(subscription.ReadOnly);
        Assert.Equal("free_people", subscription.ReadOnlyReason);
        Assert.Equal(4, subscription.FreeTier!.People);
        var refused = await RenameAsync(org, project, "Renamed");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ProblemTypes.OrganizationReadOnly, (await ProblemAsync(refused)).GetProperty("type").GetString());

        // Nobody is removed automatically; revoking one invitation is enough, with nothing to run.
        var revoked = await org.Owner.DeleteAsync($"/api/v1/orgs/{org.Slug}/invitations/{first.Invitation.Id}", Ct);
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync(Ct));
        Assert.False((await SubscriptionAsync(org)).ReadOnly);
        var renamed = await RenameAsync(org, project, "Renamed");
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync(Ct));

        // At the limit, the next person is refused with an upgrade to point at.
        var fourth = await TryInviteAsync(org, "over-4@test.local");
        await AssertFreePeopleRefusalAsync(fourth);

        // Paying lifts it: a paid organization is in nobody's free count.
        await PayAsync(org);
        var paid = await SubscriptionAsync(org);
        Assert.Equal(PlanCodes.Hosted, paid.Plan);
        Assert.False(paid.ReadOnly);
        Assert.Equal(0, paid.FreeTier!.People);
        Assert.Equal(HttpStatusCode.Created, (await TryInviteAsync(org, "over-4@test.local")).StatusCode);
    }

    // ------------------------------------------------------------------- counting

    [Fact]
    public async Task people_are_distinct_humans_across_an_owners_unpaid_organizations_with_invitations_and_without_agents()
    {
        var owner = await OwnerAsync("count");
        var alpha = await OrganizationAsync(owner, "count-alpha");
        var beta = await OrganizationAsync(owner, "count-beta");

        // One person in both organizations counts once.
        var shared = await PersonAsync("count-shared");
        await JoinAsync(alpha, shared);
        await JoinAsync(beta, shared);
        // An agent never counts.
        var agent = await beta.Owner.PostAsJsonAsync($"/api/v1/orgs/{beta.Slug}/agents",
            new CreateAgentRequest("Count Bot", null), ApiTestContext.Json, Ct);
        Assert.True(agent.IsSuccessStatusCode, await agent.Content.ReadAsStringAsync(Ct));
        // A pending invitation does.
        await InviteAsync(beta, "count-pending@test.local");

        await ExpireAsync(alpha);
        await ExpireAsync(beta);

        // Owner, the shared person and the pending invitation: three, in both organizations.
        Assert.Equal(3, (await SubscriptionAsync(alpha)).FreeTier!.People);
        Assert.Equal(3, (await SubscriptionAsync(beta)).FreeTier!.People);
        Assert.False((await SubscriptionAsync(alpha)).ReadOnly);

        // A fourth, in either organization, is refused.
        await AssertFreePeopleRefusalAsync(await TryInviteAsync(alpha, "count-fourth@test.local"));

        // Being a member of someone else's organization does not count against the member:
        // the shared person's own new free organization has room for two more.
        var mine = await OrganizationAsync(shared, "count-shared-own");
        await ExpireAsync(mine);
        Assert.Equal(1, (await SubscriptionAsync(mine)).FreeTier!.People);
        Assert.Equal(HttpStatusCode.Created, (await TryInviteAsync(mine, "count-guest@test.local")).StatusCode);
    }

    [Fact]
    public async Task accepting_an_invitation_is_the_invited_seat_and_an_acceptance_over_the_limit_is_refused()
    {
        var owner = await OwnerAsync("accept");
        var free = await OrganizationAsync(owner, "accept-free");
        var alias = await InviteAsync(free, "accept-alias@test.local");
        var other = await InviteAsync(free, "accept-other@test.local");
        await ExpireAsync(free);
        Assert.Equal(3, (await SubscriptionAsync(free)).FreeTier!.People);

        // Accepted by an account with a different address: the same seat, not a fourth.
        var real = await PersonAsync("accept-real");
        var accepted = await real.Client.PostAsync($"/api/v1/invitations/{alias.Token}/accept", null, Ct);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(Ct));
        Assert.Equal(3, (await SubscriptionAsync(free)).FreeTier!.People);

        // The Owner's other organization is still evaluating, so it may take on a fourth
        // person - who is in the Owner's count from that moment.
        var evaluating = await OrganizationAsync(owner, "accept-eval");
        await InviteAsync(evaluating, "accept-eval-mate@test.local");
        Assert.True((await SubscriptionAsync(free)).ReadOnly);

        // An acceptance in the free organization is refused while that is so.
        var invitee = await PersonAsync("accept-other");
        await AssertFreePeopleRefusalAsync(
            await invitee.Client.PostAsync($"/api/v1/invitations/{other.Token}/accept", null, Ct));
    }

    [Fact]
    public async Task ownership_that_would_take_the_new_owner_over_the_limit_is_refused()
    {
        var giver = await OwnerAsync("give");
        var shared = await OrganizationAsync(giver, "give-shared");
        var heir = await PersonAsync("give-heir");
        await JoinAsync(shared, heir);

        // The heir's own free organization is full: the heir and two more.
        var full = await OrganizationAsync(heir, "give-full");
        await InviteAsync(full, "give-full-1@test.local");
        await InviteAsync(full, "give-full-2@test.local");

        await ExpireAsync(shared);
        await ExpireAsync(full);

        var promoted = await shared.Owner.PutAsJsonAsync($"/api/v1/orgs/{shared.Slug}/members/{heir.Id}",
            new UpdateMemberRequest(OrgRole.Owner), ApiTestContext.Json, Ct);
        await AssertFreePeopleRefusalAsync(promoted);
        Assert.Equal(1, await _host.ScalarAsync(
            "SELECT count(*) FROM tenancy.organization_members WHERE organization_id = @org AND role = 0", Ct,
            ("org", shared.Id)));
    }

    [Fact]
    public async Task an_organization_with_several_owners_counts_toward_every_owner_and_removing_someone_restores_writes()
    {
        var first = await OwnerAsync("multi-first");
        var together = await OrganizationAsync(first, "multi-together");
        var second = await PersonAsync("multi-second");
        await JoinAsync(together, second);
        var promoted = await together.Owner.PutAsJsonAsync($"/api/v1/orgs/{together.Slug}/members/{second.Id}",
            new UpdateMemberRequest(OrgRole.Owner), ApiTestContext.Json, Ct);
        Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync(Ct));

        // The second Owner's own organization: the second Owner and one member.
        var own = await OrganizationAsync(second, "multi-own");
        var member = await PersonAsync("multi-member");
        await JoinAsync(own, member);

        await ExpireAsync(together);
        await ExpireAsync(own);

        // First: {first, second} = 2. Second: {second, member, first} = 3 - at the limit, not over.
        Assert.False((await SubscriptionAsync(together)).ReadOnly);
        Assert.Equal(3, (await SubscriptionAsync(together)).FreeTier!.People);

        // A pending invitation in the second Owner's own organization takes the second Owner
        // over, and so takes the shared organization with it.
        var invited = await TryInviteAsync(own, "multi-extra@test.local");
        await AssertFreePeopleRefusalAsync(invited);
        // The first Owner has room in the shared organization, but the second does not.
        await AssertFreePeopleRefusalAsync(await TryInviteAsync(together, "multi-extra@test.local"));

        // Over by other means (a member added straight to the table, as an evaluation would
        // have allowed): both organizations of that Owner are read-only...
        var extra = await PersonAsync("multi-extra");
        await _host.ExecuteAsync(
            "INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory) VALUES (@org, @user, 2, now(), true)",
            Ct, ("org", own.Id), ("user", extra.Id));
        Assert.Equal("free_people", (await SubscriptionAsync(together)).ReadOnlyReason);
        Assert.True((await SubscriptionAsync(own)).ReadOnly);

        // ...until someone is removed, with nothing else to do.
        var removed = await own.Owner.DeleteAsync($"/api/v1/orgs/{own.Slug}/members/{extra.Id}", Ct);
        Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync(Ct));
        Assert.False((await SubscriptionAsync(together)).ReadOnly);
        Assert.False((await SubscriptionAsync(own)).ReadOnly);
    }

    [Fact]
    public async Task a_paid_organization_counts_toward_nobody_and_cancelling_lands_on_the_free_tier()
    {
        var owner = await OwnerAsync("paid");
        var paid = await OrganizationAsync(owner, "paid-co");
        await PayAsync(paid);
        foreach (var n in Enumerable.Range(1, 5))
        {
            Assert.Equal(HttpStatusCode.Created, (await TryInviteAsync(paid, $"paid-{n}@test.local")).StatusCode);
        }

        var free = await OrganizationAsync(owner, "paid-free");
        await ExpireAsync(free);
        var subscription = await SubscriptionAsync(free);
        Assert.Equal(1, subscription.FreeTier!.People);
        Assert.False(subscription.ReadOnly);
        Assert.Equal(HttpStatusCode.Created, (await TryInviteAsync(free, "paid-free-1@test.local")).StatusCode);

        // The paid organization's own limits are Hosted's.
        var hosted = await SubscriptionAsync(paid);
        Assert.Equal(PlanCodes.Hosted, hosted.Plan);
        Assert.False(hosted.ReadOnly);

        // Cancelling: checkout to "free" cancels at period end, and the cancellation lands on
        // the free tier - where the six people now count, and make the Owner's organizations read-only.
        var checkout = await paid.Owner.PostAsJsonAsync($"/api/v1/orgs/{paid.Slug}/billing/checkout",
            new CheckoutRequest("free"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        Assert.Contains($"sub_{paid.Slug}", _host.Stripe.Cancellations);
        var cancelled = await _host.PostWebhookAsync(StripeEvents.Subscription("customer.subscription.deleted",
            $"evt_cancel_{paid.Slug}", $"sub_{paid.Slug}", $"cus_{paid.Slug}", "canceled", DateTimeOffset.UtcNow,
            paid.Id, plan: "hosted", items: [(BillingTestHost.HostedOrganization, 1)]), Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await _host.DrainOutboxAsync(Ct);

        var after = await SubscriptionAsync(paid);
        Assert.Equal(PlanCodes.HostedFree, after.Plan);
        Assert.True(after.ReadOnly);
        Assert.Equal("free_people", after.ReadOnlyReason);
        Assert.True((await SubscriptionAsync(free)).ReadOnly);
    }

    // ------------------------------------------------------------------ storage, runners

    [Fact]
    public async Task attachments_are_pooled_across_an_owners_unpaid_organizations()
    {
        var owner = await OwnerAsync("pool");
        var alpha = await OrganizationAsync(owner, "pool-alpha");
        var beta = await OrganizationAsync(owner, "pool-beta");
        await ExpireAsync(alpha);
        await ExpireAsync(beta);

        var (alphaItem, alphaUpload) = await ItemWithUploadAsync(alpha, "PAL");
        var committed = await CommitAsync(alpha, alphaUpload.Id, alphaItem.Id);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);

        // The second organization's own usage is zero; the Owner's pool is not.
        await CreateProjectAsync(beta, "PBE");
        var refused = await UploadAsync(beta, "PBE");
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        var problem = await ProblemAsync(refused);
        Assert.Equal(ProblemTypes.PlanLimit, problem.GetProperty("type").GetString());
        Assert.Equal("storage_bytes", problem.GetProperty("limit").GetString());
        Assert.Equal(JsonValueKind.String, problem.GetProperty("upgradeUrl").ValueKind);

        // Over the pool, reads keep working and nothing was deleted.
        var download = await alpha.Owner.GetAsync($"/api/v1/orgs/{alpha.Slug}/attachments/{alphaUpload.Id}/download", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PooledBytes, (await SubscriptionAsync(beta)).FreeTier!.StorageBytes);
        Assert.Equal(FileBytes, (await SubscriptionAsync(beta)).FreeTier!.StoredBytes);
        Assert.False((await SubscriptionAsync(beta)).ReadOnly);
    }

    [Fact]
    public async Task a_free_organization_registers_at_most_two_runners()
    {
        var owner = await OwnerAsync("runners");
        var org = await OrganizationAsync(owner, "runners-co");
        await ExpireAsync(org);

        foreach (var name in new[] { "one", "two" })
        {
            var created = await org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/runners",
                new CreateRunnerRequest(name), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var third = await org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/runners",
            new CreateRunnerRequest("three"), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.PaymentRequired, third.StatusCode);
        var problem = await ProblemAsync(third);
        Assert.Equal("runners", problem.GetProperty("limit").GetString());
        Assert.Equal(JsonValueKind.String, problem.GetProperty("upgradeUrl").ValueKind);
    }

    // --------------------------------------------------------------------------- helpers

    internal sealed record Person(string Id, string Email, HttpClient Client);

    internal sealed record Org(string Slug, Guid Id, HttpClient Owner);

    internal sealed record FreeSubscriptionDto(
        string Plan, bool ReadOnly, string? ReadOnlyReason, EvaluationView? Evaluation, FreeTierView? FreeTier,
        IReadOnlyList<PlanOptionView> Plans);

    private Task<Person> OwnerAsync(string name) => PersonAsync(name);

    private async Task<Person> PersonAsync(string name, string? email = null)
    {
        email ??= $"{name}@test.local";
        var auth = await _host.Context.RegisterAsync(email, "Free", "Tier");
        return new Person(auth.User.Id, email, _host.Context.ClientFor(auth));
    }

    /// <summary>A new organization, with the evaluation the outbox mints for it.</summary>
    private async Task<Org> OrganizationAsync(Person owner, string slug)
    {
        var response = await owner.Client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(slug, slug, null, null), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var organization = (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!;
        await _host.DrainOutboxAsync(Ct);
        return new Org(slug, organization.Id, owner.Client);
    }

    private Task ExpireAsync(Org org) => _host.ExecuteAsync(
        "UPDATE billing.evaluations SET started_at = now() - interval '31 days', ends_at = now() - interval '1 day' WHERE organization_id = @org",
        Ct, ("org", org.Id));

    private async Task PayAsync(Org org)
    {
        var paid = await _host.PostWebhookAsync(StripeEvents.CheckoutCompleted(
            $"evt_pay_{org.Slug}", org.Id, $"cus_{org.Slug}", $"sub_{org.Slug}", "hosted", DateTimeOffset.UtcNow.AddMinutes(-5)), Ct);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        await _host.DrainOutboxAsync(Ct);
    }

    private Task<HttpResponseMessage> TryInviteAsync(Org org, string email) =>
        org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/invitations",
            new CreateInvitationRequest(email, OrgRole.Member, null, null), ApiTestContext.Json, Ct);

    private async Task<InvitationLink> InviteAsync(Org org, string email)
    {
        var response = await TryInviteAsync(org, email);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<InvitationLink>(ApiTestContext.Json, Ct))!;
    }

    private async Task JoinAsync(Org org, Person person)
    {
        var link = await InviteAsync(org, person.Email);
        var accepted = await person.Client.PostAsync($"/api/v1/invitations/{link.Token}/accept", null, Ct);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    private async Task<FreeSubscriptionDto> SubscriptionAsync(Org org)
    {
        var response = await org.Owner.GetAsync($"/api/v1/orgs/{org.Slug}/billing/subscription", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<FreeSubscriptionDto>(ApiTestContext.Json, Ct))!;
    }

    private Task<HttpResponseMessage> TryCreateProjectAsync(Org org, string key) =>
        org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/projects",
            new CreateProjectRequest($"Project {key}", key, null, null, null, null), ApiTestContext.Json, Ct);

    private Task<HttpResponseMessage> RenameAsync(Org org, ProjectView project, string name) =>
        org.Owner.PatchAsJsonAsync($"/api/v1/orgs/{org.Slug}/projects/{project.Key}",
            new UpdateProjectRequest(name, null, null, null, null, project.Version), ApiTestContext.Json, Ct);

    private async Task<ProjectView> CreateProjectAsync(Org org, string key)
    {
        var response = await TryCreateProjectAsync(org, key);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<(WorkItemView Item, AttachmentView Upload)> ItemWithUploadAsync(Org org, string key)
    {
        await CreateProjectAsync(org, key);
        var item = await org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/projects/{key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Holds a file", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(item.IsSuccessStatusCode, await item.Content.ReadAsStringAsync(Ct));
        var upload = await UploadAsync(org, key);
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(Ct));
        return ((await item.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!,
            (await upload.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, Ct))!);
    }

    /// <summary>Text, not an image: images are re-encoded to WebP and would not be this size.</summary>
    private async Task<HttpResponseMessage> UploadAsync(Org org, string key)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Enumerable.Repeat((byte)'a', FileBytes).ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "notes.txt");
        return await org.Owner.PostAsync($"/api/v1/orgs/{org.Slug}/projects/{key}/attachments", form, Ct);
    }

    private Task<HttpResponseMessage> CommitAsync(Org org, Guid attachmentId, Guid itemId) =>
        org.Owner.PostAsJsonAsync($"/api/v1/orgs/{org.Slug}/attachments/{attachmentId}/commit",
            new CommitAttachmentRequest(itemId, null), ApiTestContext.Json, Ct);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task AssertFreePeopleRefusalAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.PaymentRequired, await response.Content.ReadAsStringAsync(Ct));
        var problem = await ProblemAsync(response);
        Assert.Equal(ProblemTypes.PlanLimit, problem.GetProperty("type").GetString());
        Assert.Equal("free_people", problem.GetProperty("limit").GetString());
        Assert.Equal(JsonValueKind.String, problem.GetProperty("upgradeUrl").ValueKind);
        Assert.Contains("free plan allows 3 people", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }
}

/// <summary>
/// With the switch off - the default - a hosted instance behaves exactly as before the free
/// tier: an evaluation ends in read-only, the legacy Free is the cancellation target, and
/// nothing is counted across organizations.
/// </summary>
[Trait("Category", "Billing")]
public sealed class FreeTierSwitchedOffTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private BillingTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await BillingTestHost.CreateAsync(postgres, garage, "free_tier_off");

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task an_evaluation_still_ends_in_read_only_and_no_one_is_counted()
    {
        var (owner, orgId, _) = await _host.CreateOrganizationAsync("off-co", Ct);
        await _host.DrainOutboxAsync(Ct);
        foreach (var n in Enumerable.Range(1, 4))
        {
            var invited = await owner.PostAsJsonAsync("/api/v1/orgs/off-co/invitations",
                new CreateInvitationRequest($"off-{n}@test.local", OrgRole.Member, null, null), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        }
        await _host.ExecuteAsync(
            "UPDATE billing.evaluations SET started_at = now() - interval '31 days', ends_at = now() - interval '1 day'", Ct);

        var subscription = (await owner.GetFromJsonAsync<FreeTierTests.FreeSubscriptionDto>(
            "/api/v1/orgs/off-co/billing/subscription", ApiTestContext.Json, Ct))!;
        Assert.Equal(PlanCodes.Hosted, subscription.Plan);
        Assert.True(subscription.ReadOnly);
        Assert.Equal("evaluation_ended", subscription.ReadOnlyReason);
        Assert.Null(subscription.FreeTier);
        Assert.DoesNotContain(subscription.Plans, p => p.Code == PlanCodes.HostedFree);

        // Checkout does not know the free tier exists.
        var checkout = await owner.PostAsJsonAsync("/api/v1/orgs/off-co/billing/checkout",
            new CheckoutRequest(PlanCodes.HostedFree), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, checkout.StatusCode);

        using var scope = _host.Context.Factory.Services.CreateScope();
        Assert.Equal(90, await scope.ServiceProvider.GetRequiredService<IPlanAllowances>()
            .GetRunLogRetentionDaysAsync(orgId, Ct));
    }
}

/// <summary>Self-hosted ignores the free tier entirely, even with the switch on.</summary>
[Trait("Category", "Billing")]
public sealed class FreeTierSelfHostedTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private BillingTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _host = await BillingTestHost.CreateAsync(postgres, garage, "free_tier_self", mode: "self_hosted",
            configure: settings => settings["Billing:FreeTier:Enabled"] = "true");

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task nothing_is_counted_limited_or_offered()
    {
        var (owner, _, _) = await _host.CreateOrganizationAsync("self-co", Ct);
        await _host.DrainOutboxAsync(Ct);
        Assert.Equal(0, await _host.ScalarAsync("SELECT count(*) FROM billing.evaluations", Ct));

        foreach (var n in Enumerable.Range(1, 5))
        {
            var invited = await owner.PostAsJsonAsync("/api/v1/orgs/self-co/invitations",
                new CreateInvitationRequest($"self-{n}@test.local", OrgRole.Member, null, null), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        }
        foreach (var name in new[] { "one", "two", "three" })
        {
            var created = await owner.PostAsJsonAsync("/api/v1/orgs/self-co/runners",
                new CreateRunnerRequest(name), ApiTestContext.Json, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var subscription = (await owner.GetFromJsonAsync<FreeTierTests.FreeSubscriptionDto>(
            "/api/v1/orgs/self-co/billing/subscription", ApiTestContext.Json, Ct))!;
        Assert.Equal(PlanCodes.SelfHosted, subscription.Plan);
        Assert.False(subscription.ReadOnly);
        Assert.Null(subscription.FreeTier);
        Assert.Empty(subscription.Plans);
    }
}
