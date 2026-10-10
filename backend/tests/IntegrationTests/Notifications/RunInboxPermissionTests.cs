using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
public sealed class RunInboxPermissionTests(PostgresFixture postgres, GarageFixture garage)
{
    [Theory]
    [InlineData(NotificationKind.RunSucceeded)]
    [InlineData(NotificationKind.RunFailed)]
    [InlineData(NotificationKind.RunNeedsInput)]
    public async Task inbox_and_unread_count_apply_current_factory_permission_before_paging(NotificationKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(postgres, garage, "run_inbox", appRole: true);
        var organization = await CreateOrganizationAsync(context, "stakeholder-inbox", ct);
        var permittedOrganization = await CreateOrganizationAsync(context, "operator-inbox", ct);
        var member = await context.RegisterAsync("inbox-member@test.local");
        using var client = context.ClientFor(member);
        await AddMemberAsync(context, organization.Id, member.User.Id, false, ct);
        await AddMemberAsync(context, permittedOrganization.Id, member.User.Id, true, ct);
        var admin = await context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        var older = DateTimeOffset.UtcNow.AddDays(-1);
        var ordinary = Notification(organization.Id, member.User.Id, NotificationKind.Transitioned,
            "The source item changed state", older);
        var permittedRun = Notification(permittedOrganization.Id, member.User.Id, kind, "Operator run", older);

        await using (var scope = context.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>();
            using var organizationScope = tenant.Use(organization.Id);
            db.Notifications.Add(ordinary);
            // Enough newer forbidden rows to consume the whole page if filtering happens after LIMIT.
            // These also represent legacy run notifications without a RunId.
            for (var i = 0; i < 205; i++)
                db.Notifications.Add(Notification(organization.Id, member.User.Id, kind,
                    "Stakeholder Factory detail", DateTimeOffset.UtcNow));
            db.Notifications.Add(Notification(organization.Id, admin.User.Id, kind, "Owner run", older));
            await db.SaveChangesAsync(ct);
            using var permittedScope = tenant.Use(permittedOrganization.Id);
            db.Notifications.Add(permittedRun);
            db.Notifications.Add(Notification(permittedOrganization.Id, admin.User.Id, kind, "Other owner run", older));
            await db.SaveChangesAsync(ct);
        }

        foreach (var path in new[] { "/api/v1/me/notifications", "/api/v1/me/notifications?unread=true" })
        {
            var inbox = (await client.GetFromJsonAsync<List<NotificationView>>(path, ApiTestContext.Json, ct))!;
            Assert.Equal(2, inbox.Count);
            Assert.Contains(inbox, x => x.Id == ordinary.Id);
            Assert.Contains(inbox, x => x.Id == permittedRun.Id && x.OrganizationId == permittedOrganization.Id);
            Assert.DoesNotContain(inbox, x => x.Kind == kind && x.OrganizationId == organization.Id);
        }
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(2, session!.UnreadCount);
        var ownerInbox = (await context.Admin.GetFromJsonAsync<List<NotificationView>>(
            "/api/v1/me/notifications", ApiTestContext.Json, ct))!;
        Assert.Equal(2, ownerInbox.Count);
        Assert.All(ownerInbox, x => Assert.Equal(kind, x.Kind));

        // Revoke after the first reads have populated authorization caches.
        var revoke = await context.Admin.PutAsJsonAsync(
            $"/api/v1/orgs/operator-inbox/members/{member.User.Id}", new UpdateMemberRequest(null, false),
            ApiTestContext.Json, ct);
        revoke.EnsureSuccessStatusCode();
        var afterRevoke = (await client.GetFromJsonAsync<List<NotificationView>>(
            "/api/v1/me/notifications", ApiTestContext.Json, ct))!;
        Assert.Equal(ordinary.Id, Assert.Single(afterRevoke).Id);
        session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(1, session!.UnreadCount);

        var mark = await client.PostAsJsonAsync("/api/v1/me/notifications/read",
            new MarkNotificationsReadRequest([ordinary.Id]), ApiTestContext.Json, ct);
        mark.EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<List<NotificationView>>(
            "/api/v1/me/notifications?unread=true", ApiTestContext.Json, ct))!);
        session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(0, session!.UnreadCount);
    }

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

    private static Notification Notification(Guid organizationId, string userId, NotificationKind kind,
        string message, DateTimeOffset createdAt) => new()
    {
        OrganizationId = organizationId, UserId = userId, EventId = Guid.NewGuid(), Kind = kind,
        Message = message, CreatedAt = createdAt
    };
}
