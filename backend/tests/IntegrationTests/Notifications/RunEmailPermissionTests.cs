using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Delivery;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Events;
using Aictiq.Modules.Notifications.Templates;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class RunEmailPermissionTests(PostgresFixture postgres, GarageFixture garage)
{
    [Theory]
    [InlineData(RunOutcomes.Succeeded, false, NotificationKind.RunSucceeded)]
    [InlineData(RunOutcomes.Failed, false, NotificationKind.RunFailed)]
    [InlineData(RunOutcomes.TimedOut, false, NotificationKind.RunFailed)]
    [InlineData(RunOutcomes.Cancelled, false, NotificationKind.RunFailed)]
    [InlineData(RunOutcomes.Succeeded, true, NotificationKind.RunNeedsInput)]
    public async Task run_events_exclude_stakeholder_requesters_and_assignees_but_email_operators(
        string outcome, bool needsInput, NotificationKind expectedKind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(appRole: true);
        var organization = await CreateOrganizationAsync(context, "run-mail", ct);
        var projectResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/run-mail/projects",
            new CreateProjectRequest("Web", "WEB", null, ProjectVisibility.Organization, null, null), ApiTestContext.Json, ct);
        projectResponse.EnsureSuccessStatusCode();
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, ct))!;
        var stakeholder = await context.RegisterAsync("stakeholder@test.local");
        await AddMemberAsync(context, organization.Id, stakeholder.User.Id, false, ct);
        var itemResponse = await context.Admin.PostAsJsonAsync("/api/v1/orgs/run-mail/projects/WEB/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Ship the change", null, null, null,
                stakeholder.User.Id, null, null, null, null, null, null, null, null), ApiTestContext.Json, ct);
        itemResponse.EnsureSuccessStatusCode();
        var item = (await itemResponse.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, ct))!;

        RunFinished Finished(string requestedBy) => new(organization.Id, project.Id, item.Id, item.Key,
            Guid.NewGuid(), "agent", outcome, null, null, "Completed work", null, "Operator-only failure detail")
            { RequestedBy = requestedBy, NeedsInput = needsInput, Refinement = needsInput };

        await HandleAsync(context, Finished(stakeholder.User.Id), ct);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organization.Id);
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        Assert.Empty(await db.Notifications.ToListAsync(ct));
        Assert.Empty(await db.EmailOutbox.ToListAsync(ct));

        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        var eligibleEvent = Finished(admin.User.Id);
        await HandleAsync(context, eligibleEvent, ct);
        var notification = Assert.Single(await db.Notifications.ToListAsync(ct));
        Assert.Equal(admin.User.Id, notification.UserId);
        Assert.Equal(expectedKind, notification.Kind);
        var mail = Assert.Single(await db.EmailOutbox.ToListAsync(ct));
        Assert.Equal(ApiTestContext.AdminEmail, mail.ToAddress);
        Assert.Contains($"/o/run-mail/factory/runs/{eligibleEvent.RunId}", mail.BodyText);
        Assert.DoesNotContain("Operator-only failure detail", mail.BodyText);

        await HandleAsync(context, eligibleEvent, ct);
        Assert.Equal(1, await db.EmailOutbox.CountAsync(ct));
    }

    [Theory]
    [InlineData(NotificationKind.RunSucceeded)]
    [InlineData(NotificationKind.RunFailed)]
    [InlineData(NotificationKind.RunNeedsInput)]
    public async Task immediate_delivery_rechecks_factory_permission_for_stored_run_notifications(NotificationKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync(appRole: true);
        var organization = await CreateOrganizationAsync(context, "immediate-mail", ct);
        var otherOrganization = await CreateOrganizationAsync(context, "other-immediate", ct);
        var member = await context.RegisterAsync("member@test.local");
        await AddMemberAsync(context, organization.Id, member.User.Id, false, ct);
        await AddMemberAsync(context, otherOrganization.Id, member.User.Id, true, ct);
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<NotificationEmailService>();
        // Older saved rows may have no RunId. The kind still identifies operator mail.
        var suppressed = Notification(organization.Id, member.User.Id, kind, "Stakeholder run notification");
        var ordinary = Notification(organization.Id, member.User.Id, NotificationKind.Transitioned, "A watched item changed state");
        var permitted = Notification(otherOrganization.Id, member.User.Id, kind, "Permitted organization run");
        var owner = Notification(organization.Id, admin.User.Id, kind, "Owner run notification");
        await email.QueueImmediateAsync([suppressed, ordinary, permitted, owner], ct);

        var queued = await db.EmailOutbox.ToListAsync(ct);
        Assert.Equal(3, queued.Count);
        Assert.DoesNotContain(queued, m => m.Id == suppressed.Id);
        Assert.Contains(queued, m => m.Id == ordinary.Id && m.BodyText.Contains(ordinary.Message, StringComparison.Ordinal));
        Assert.Contains(queued, m => m.Id == permitted.Id);
        Assert.Contains(queued, m => m.Id == owner.Id);
    }

    [Fact]
    public async Task operator_immediate_mail_still_respects_preferences_and_active_presence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync();
        var organization = await CreateOrganizationAsync(context, "operator-preferences", ct);
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        await using var scope = context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.Preferences.AddRange(
            new NotificationPreference { UserId = admin.User.Id, Kind = NotificationKind.RunSucceeded, EmailMode = EmailNotificationMode.Off },
            new NotificationPreference { UserId = admin.User.Id, Kind = NotificationKind.RunFailed, EmailMode = EmailNotificationMode.Digest });
        await db.SaveChangesAsync(ct);
        var email = scope.ServiceProvider.GetRequiredService<NotificationEmailService>();
        await email.QueueImmediateAsync([
            Notification(organization.Id, admin.User.Id, NotificationKind.RunSucceeded, "Email is off"),
            Notification(organization.Id, admin.User.Id, NotificationKind.RunFailed, "Wait for the digest")], ct);
        Assert.Empty(await db.EmailOutbox.ToListAsync(ct));

        db.Presence.Add(new NotificationPresence { UserId = admin.User.Id, LastSeenAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        await email.QueueImmediateAsync([
            Notification(organization.Id, admin.User.Id, NotificationKind.RunNeedsInput, "Already active in the app")], ct);
        Assert.Empty(await db.EmailOutbox.ToListAsync(ct));
    }

    [Fact]
    public async Task digest_rechecks_revoked_and_organization_specific_permissions_without_suppressing_ordinary_mail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await CreateAsync();
        var organization = await CreateOrganizationAsync(context, "digest-mail", ct);
        var otherOrganization = await CreateOrganizationAsync(context, "other-digest", ct);
        var revoked = await context.RegisterAsync("revoked@test.local");
        var crossOrganization = await context.RegisterAsync("cross-org@test.local");
        var stakeholder = await context.RegisterAsync("only-runs@test.local");
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        await AddMemberAsync(context, organization.Id, revoked.User.Id, true, ct);
        await AddMemberAsync(context, organization.Id, crossOrganization.User.Id, false, ct);
        await AddMemberAsync(context, otherOrganization.Id, crossOrganization.User.Id, true, ct);
        await AddMemberAsync(context, organization.Id, stakeholder.User.Id, false, ct);
        var runKinds = new[] { NotificationKind.RunSucceeded, NotificationKind.RunFailed, NotificationKind.RunNeedsInput };
        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
            using var organizationScope = tenant.Use(organization.Id);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IProjectAccess>()
                .CanOperateFactoryAsync(revoked.User.Id, organization.Id, ct));
            foreach (var userId in new[] { revoked.User.Id, crossOrganization.User.Id, stakeholder.User.Id, admin.User.Id })
            {
                foreach (var kind in runKinds.Append(NotificationKind.Transitioned))
                    db.Preferences.Add(new NotificationPreference { UserId = userId, Kind = kind, EmailMode = EmailNotificationMode.Digest });
                foreach (var kind in runKinds)
                    db.Notifications.Add(Notification(organization.Id, userId, kind, $"Excluded or allowed {userId} {kind}"));
            }
            db.Notifications.Add(Notification(organization.Id, revoked.User.Id, NotificationKind.Transitioned, "Revoked member ordinary update"));
            db.Notifications.Add(Notification(organization.Id, crossOrganization.User.Id, NotificationKind.Transitioned, "Cross organization ordinary update"));
            await db.SaveChangesAsync(ct);
            using var otherOrganizationScope = tenant.Use(otherOrganization.Id);
            foreach (var kind in runKinds)
                db.Notifications.Add(Notification(otherOrganization.Id, crossOrganization.User.Id, kind, $"Allowed other organization {kind}"));
            await db.SaveChangesAsync(ct);
        }

        // Revoke after storing the notifications, using the endpoint that invalidates the membership cache.
        var revoke = await context.Admin.PutAsJsonAsync($"/api/v1/orgs/digest-mail/members/{revoked.User.Id}",
            new UpdateMemberRequest(null, false), ApiTestContext.Json, ct);
        revoke.EnsureSuccessStatusCode();
        var digestAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(8).AddMinutes(10), TimeSpan.Zero);
        var digest = new DailyNotificationDigestService(context.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(digestAt), NullLogger<DailyNotificationDigestService>.Instance);
        await digest.RunOnceAsync(ct);
        await digest.RunOnceAsync(ct);

        await using var inspection = context.Factory.Services.CreateAsyncScope();
        var notifications = inspection.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var queued = await notifications.EmailOutbox.ToListAsync(ct);
        Assert.Equal(3, queued.Count);
        Assert.All(queued, m => Assert.Equal("digest", m.Template));
        Assert.DoesNotContain(queued, m => m.ToAddress == "only-runs@test.local");
        Assert.False(await notifications.Digests.AnyAsync(d => d.UserId == stakeholder.User.Id, ct));
        var revokedMail = Assert.Single(queued, m => m.ToAddress == "revoked@test.local");
        Assert.Contains("Revoked member ordinary update", revokedMail.BodyText);
        Assert.DoesNotContain("Excluded or allowed", revokedMail.BodyText);
        var crossMail = Assert.Single(queued, m => m.ToAddress == "cross-org@test.local");
        Assert.Contains("Cross organization ordinary update", crossMail.BodyText);
        Assert.DoesNotContain("Excluded or allowed", crossMail.BodyText);
        var ownerMail = Assert.Single(queued, m => m.ToAddress == ApiTestContext.AdminEmail);
        foreach (var kind in runKinds)
        {
            Assert.Contains($"Allowed other organization {kind}", crossMail.BodyText);
            Assert.Contains($"Excluded or allowed {admin.User.Id} {kind}", ownerMail.BodyText);
        }
        Assert.Equal(3, await notifications.Digests.CountAsync(ct));
    }

    private Task<ApiTestContext> CreateAsync(bool appRole = false) =>
        ApiTestContext.CreateAsync(postgres, garage, "run_email", appRole: appRole,
            configure: settings => settings["Email:BaseUrl"] = "https://aictiq.test/",
            configureServices: services =>
            {
                services.AddOptions<NotificationEmailOptions>();
                services.AddSingleton<EmailTemplateRenderer>();
                services.AddScoped<NotificationEmailService>();
            });

    private static async Task<OrganizationView> CreateOrganizationAsync(ApiTestContext context, string slug, CancellationToken ct)
    {
        var response = await context.Admin.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(slug, slug, null, null), ApiTestContext.Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, ct))!;
    }

    private static async Task AddMemberAsync(ApiTestContext context, Guid organizationId, string userId, bool operates, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(context.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tenancy.organization_members (organization_id, user_id, role, joined_at, can_operate_factory)
            VALUES (@org, @user, @role, now(), @operates)
            """, connection);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("role", (int)OrgRole.Member);
        command.Parameters.AddWithValue("operates", operates);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Notification Notification(Guid organizationId, string userId, NotificationKind kind, string message) => new()
    {
        OrganizationId = organizationId, UserId = userId, EventId = Guid.NewGuid(), Kind = kind,
        Message = message, CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task HandleAsync(ApiTestContext context, RunFinished e, CancellationToken ct)
    {
        await using var scope = context.Factory.Services.CreateAsyncScope();
        await ActivatorUtilities.CreateInstance<RunNotificationHandler>(scope.ServiceProvider).HandleAsync(e, ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
