using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Notifications.Domain;
using Aictiq.Modules.Notifications.Endpoints;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.IntegrationTests.Notifications;

[Trait("Category", "Notifications")]
[Collection("postgres")]
public sealed class NotificationEndpointsTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext Context { get; set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Context = await ApiTestContext.CreateAsync(postgres, garage, "notifications");
        var session = await Context.LoginAsync(ApiTestContext.AdminEmail, ApiTestContext.AdminPassword);
        await using var scope = Context.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var organizationId = Guid.NewGuid();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(organizationId);
        db.Notifications.Add(new Notification
        {
            OrganizationId = organizationId, UserId = session.User.Id, EventId = Guid.NewGuid(),
            Kind = NotificationKind.Mentioned, Message = "You were mentioned in a comment.", CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task inbox_reads_rows_preferences_and_session_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var inbox = await Context.Admin.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications?unread=true", ApiTestContext.Json, ct);
        var entry = Assert.Single(inbox!);

        var session = await Context.Admin.GetFromJsonAsync<SessionResponse>("/api/v1/auth/session", ApiTestContext.Json, ct);
        Assert.Equal(1, session!.UnreadCount);

        var mark = await Context.Admin.PostAsJsonAsync("/api/v1/me/notifications/read", new MarkNotificationsReadRequest([entry.Id]), ApiTestContext.Json, ct);
        mark.EnsureSuccessStatusCode();
        Assert.Empty((await Context.Admin.GetFromJsonAsync<List<NotificationView>>("/api/v1/me/notifications?unread=true", ApiTestContext.Json, ct))!);

        var preferences = new PutNotificationPreferencesRequest([new(NotificationKind.Mentioned, false, true)]);
        var put = await Context.Admin.PutAsJsonAsync("/api/v1/me/notification-preferences", preferences, ApiTestContext.Json, ct);
        put.EnsureSuccessStatusCode();
        var saved = await put.Content.ReadFromJsonAsync<List<NotificationPreferenceView>>(ApiTestContext.Json, ct);
        Assert.Contains(saved!, x => x.Kind == NotificationKind.Mentioned && !x.InApp && x.Email);
    }

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}
