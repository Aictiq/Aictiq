using Aictiq.Modules.Notifications.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Aictiq.Modules.Notifications.Delivery;

namespace Aictiq.Modules.Notifications.Endpoints;

public sealed record NotificationView(Guid Id, NotificationKind Kind, Guid? ProjectId, Guid? ItemId, string? ItemKey, string Message, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
public sealed record MarkNotificationsReadRequest(IReadOnlyList<Guid>? Ids, bool All = false);
/// <summary>
/// <c>email</c> is the persisted delivery mode. The boolean constructor/property remain
/// source-compatible for older API clients while their wire representation upgrades to
/// off/immediate/digest.
/// </summary>
public sealed record NotificationPreferenceView
{
    public NotificationPreferenceView() { }
    public NotificationPreferenceView(NotificationKind kind, bool inApp, bool email)
        : this(kind, inApp, email ? EmailNotificationMode.Immediate : EmailNotificationMode.Off) { }
    public NotificationPreferenceView(NotificationKind kind, bool inApp, EmailNotificationMode email)
        => (Kind, InApp, EmailMode) = (kind, inApp, email);
    public NotificationKind Kind { get; init; }
    public bool InApp { get; init; }
    [JsonIgnore] public bool Email => EmailMode != EmailNotificationMode.Off;
    [JsonPropertyName("email")] public EmailNotificationMode EmailMode { get; init; }
}
public sealed record PutNotificationPreferencesRequest(IReadOnlyList<NotificationPreferenceView>? Preferences);

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Notifications").RequireAuthorization();
        me.MapGet("/notifications", List).RequireScope(Scopes.Read);
        me.MapPost("/notifications/read", MarkRead).RequireScope(Scopes.Write);
        me.MapGet("/notification-preferences", Preferences).RequireScope(Scopes.Read);
        me.MapPut("/notification-preferences", PutPreferences).RequireScope(Scopes.Write);
        api.MapGet("/email/unsubscribe", Unsubscribe).AllowAnonymous().WithTags("Notifications");
        return api;
    }
    // Nullable, like every other optional query flag: a non-nullable bool is *required*
    // by minimal-API binding, so the inbox — which sends the flag only when filtering —
    // got a 400 rather than its list.
    private static async Task<IResult> List(bool? unread, NotificationsDbContext db, ICurrentUser user, CancellationToken ct) => Results.Ok(await db.Notifications.IgnoreQueryFilters().AsNoTracking().Where(x => x.UserId == user.UserId && (unread != true || x.ReadAt == null)).OrderByDescending(x => x.CreatedAt).Take(200).Select(x => new NotificationView(x.Id, x.Kind, x.ProjectId, x.ItemId, x.ItemKey, x.Message, x.CreatedAt, x.ReadAt)).ToListAsync(ct));
    private static async Task<IResult> MarkRead(MarkNotificationsReadRequest request, NotificationsDbContext db, ICurrentUser user, TimeProvider clock, CancellationToken ct) { var query = db.Notifications.IgnoreQueryFilters().Where(x => x.UserId == user.UserId && x.ReadAt == null); if (!request.All) { var ids = request.Ids?.Distinct().ToArray() ?? []; if (ids.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = ["Provide notification ids or all=true."] }); query = query.Where(x => ids.Contains(x.Id)); } var count = await query.ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAt, clock.GetUtcNow()), ct); return Results.Ok(new { read = count }); }
    private static async Task<IResult> Preferences(NotificationsDbContext db, ICurrentUser user, CancellationToken ct) => Results.Ok(await db.Preferences.AsNoTracking().Where(x => x.UserId == user.UserId).Select(x => new NotificationPreferenceView(x.Kind, x.InApp, x.EmailMode)).ToListAsync(ct));
    private static async Task<IResult> PutPreferences(PutNotificationPreferencesRequest request, NotificationsDbContext db, ICurrentUser user, CancellationToken ct) { foreach (var value in request.Preferences ?? []) { var preference = await db.Preferences.SingleOrDefaultAsync(x => x.UserId == user.UserId && x.Kind == value.Kind, ct); if (preference is null) db.Preferences.Add(new NotificationPreference { UserId = user.UserId!, Kind = value.Kind, InApp = value.InApp, EmailMode = value.EmailMode }); else { preference.InApp = value.InApp; preference.EmailMode = value.EmailMode; } } await db.SaveChangesAsync(ct); return await Preferences(db, user, ct); }
    private static async Task<IResult> Unsubscribe(string? token, NotificationsDbContext db, NotificationUnsubscribeTokens tokens, CancellationToken ct)
    {
        if (!tokens.TryRead(token, out var userId, out var kind)) return Results.BadRequest(new { error = "The unsubscribe link is invalid or expired." });
        var preference = await db.Preferences.SingleOrDefaultAsync(x => x.UserId == userId && x.Kind == kind, ct);
        if (preference is null) db.Preferences.Add(new NotificationPreference { UserId = userId, Kind = kind, EmailMode = EmailNotificationMode.Off });
        else preference.EmailMode = EmailNotificationMode.Off;
        await db.SaveChangesAsync(ct);
        return Results.Content("<p>You will no longer receive these email notifications.</p>", "text/html");
    }
}
