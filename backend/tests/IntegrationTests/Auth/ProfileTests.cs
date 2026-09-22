using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.SharedKernel.Storage;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// A person's own account: name, time zone, picture, and the list of places they are
/// signed in.
///
/// The picture carries most of this file, because it is the first thing in Aictiq to use
/// the presigned-upload path end to end: the API hands out a signed PUT, the bytes go
/// straight to the store, and the commit is where the rules are actually enforced - the
/// signature can fix a content type but cannot bound a size.
/// </summary>
[Trait("Category", "Auth")]
[Collection("postgres")]
public sealed class ProfileTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private HttpClient _store = null!;
    private string _userId = null!;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "profile");
        var auth = await _context.RegisterAsync("ada@test.local", "Ada", "Lovelace");
        _client = _context.ClientFor(auth);
        _userId = auth.User.Id;
        // A plain client: the presigned URL is another origin and the signature is the
        // only credential it takes.
        _store = new HttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        _client.Dispose();
        await _context.DisposeAsync();
    }

    // ------------------------------------------------------------------------ the profile

    [Fact]
    public async Task the_profile_says_who_you_are_and_how_you_sign_in()
    {
        var ct = TestContext.Current.CancellationToken;

        var profile = await GetAsync(ct);

        Assert.Equal("ada@test.local", profile.Email);
        Assert.Equal("Ada Lovelace", profile.FullName);
        Assert.True(profile.HasPassword);
        Assert.False(profile.IsAgent);
        Assert.Null(profile.AvatarKey);
        // Null rather than a default: a person with no zone of their own follows the
        // organization's, and inventing one here would hide that.
        Assert.Null(profile.TimeZone);
        Assert.Null(profile.PendingEmail);
    }

    [Fact]
    public async Task a_name_and_a_time_zone_can_be_changed_and_cleared()
    {
        var ct = TestContext.Current.CancellationToken;

        var updated = await PatchAsync(new UpdateProfileRequest("Augusta", null, "Europe/Sarajevo"), ct);
        Assert.Equal("Augusta", updated.FirstName);
        // Absent means "leave it alone", so the surname survived.
        Assert.Equal("Lovelace", updated.LastName);
        Assert.Equal("Europe/Sarajevo", updated.TimeZone);

        var cleared = await PatchAsync(new UpdateProfileRequest(null, null, ""), ct);
        Assert.Null(cleared.TimeZone);
    }

    [Fact]
    public async Task a_time_zone_the_server_cannot_resolve_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PatchAsJsonAsync("/api/v1/me",
            new UpdateProfileRequest(null, null, "Mars/Olympus"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("timeZone", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_blank_name_is_refused_rather_than_saved()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PatchAsJsonAsync("/api/v1/me",
            new UpdateProfileRequest("   ", null, null), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------------- the picture

    [Fact]
    public async Task a_picture_is_uploaded_to_the_store_and_only_its_key_reaches_the_api()
    {
        var ct = TestContext.Current.CancellationToken;

        var ticket = await PresignAsync("image/png", 64, ct);
        Assert.StartsWith($"users/{_userId}/avatar/", ticket.Key, StringComparison.Ordinal);

        await PutAsync(ticket.UploadUrl, "image/png", Png(64), ct);

        var profile = await CommitAsync(ticket.Key, ct);
        Assert.Equal(ticket.Key, profile.AvatarKey);
        // The version is what the client hangs on the URL, so a new picture is a new URL.
        Assert.EndsWith(profile.AvatarVersion!, ticket.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task the_avatar_route_redirects_to_the_store_and_may_be_cached()
    {
        var ct = TestContext.Current.CancellationToken;
        await UploadAsync(ct);

        // The factory's client follows redirects by default, and this one leads out of
        // the test server into the store's container. Read it rather than chase it.
        using var noFollow = _context.Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        noFollow.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;

        var response = await noFollow.GetAsync($"/api/v1/users/{_userId}/avatar", ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("X-Amz-Signature", response.Headers.Location!.Query, StringComparison.Ordinal);
        // The API's blanket no-store would make an <img> re-ask on every screen. This one
        // response opted out, and the signature outlives the cache window by design.
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.Equal(Avatar.CacheLifetime, response.Headers.CacheControl.MaxAge);
        Assert.True(Avatar.CacheLifetime < Avatar.DownloadTtl);
    }

    [Fact]
    public async Task someone_without_a_picture_is_a_404_and_so_is_someone_who_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;

        var mine = await _client.GetAsync($"/api/v1/users/{_userId}/avatar", ct);
        var nobody = await _client.GetAsync($"/api/v1/users/{Guid.NewGuid()}/avatar", ct);

        // Identical on purpose: the caller draws initials either way, and a distinct
        // answer would tell an authenticated stranger which ids are real.
        Assert.Equal(HttpStatusCode.NotFound, mine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
    }

    [Fact]
    public async Task a_key_belonging_to_somebody_else_cannot_be_claimed_as_your_picture()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PutAsJsonAsync("/api/v1/me/avatar",
            new AvatarCommitRequest($"users/{Guid.NewGuid()}/avatar/stolen.png"), ct);

        // Without this, "commit my avatar" would accept any key in the bucket and turn a
        // profile picture into a presigned read of someone else's attachment.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task an_object_over_the_limit_is_refused_and_deleted_rather_than_kept()
    {
        var ct = TestContext.Current.CancellationToken;

        var ticket = await PresignAsync("image/png", 1024, ct);
        // The signature fixes the content type but cannot bound the length, so the client
        // is free to send more than it asked to. The commit is the guard.
        await PutAsync(ticket.UploadUrl, "image/png", Png((int)Avatar.MaxBytes + 1024), ct);

        var response = await _client.PutAsJsonAsync("/api/v1/me/avatar",
            new AvatarCommitRequest(ticket.Key), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // And it is not left in the bucket, paid for and referenced by nothing.
        Assert.False(await ExistsInStoreAsync(ticket.Key, ct));
    }

    [Fact]
    public async Task an_svg_is_not_an_avatar()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync("/api/v1/me/avatar",
            new AvatarUploadRequest("image/svg+xml", 512), ct);

        // A document that can carry script, served from an origin no CSP of ours reaches.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task replacing_a_picture_deletes_the_one_it_replaced()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await UploadAsync(ct);
        var second = await UploadAsync(ct);
        Assert.NotEqual(first, second);

        // Nothing points at the old key any more, so nothing should still be stored under it.
        Assert.False(await ExistsInStoreAsync(first, ct));
        Assert.True(await ExistsInStoreAsync(second, ct));
    }

    [Fact]
    public async Task removing_a_picture_clears_the_row_and_the_object()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await UploadAsync(ct);

        var response = await _client.DeleteAsync("/api/v1/me/avatar", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Null((await GetAsync(ct)).AvatarKey);
        Assert.False(await ExistsInStoreAsync(key, ct));
    }

    // ------------------------------------------------------------------------- the sessions

    [Fact]
    public async Task every_sign_in_is_a_session_and_the_caller_can_tell_which_is_theirs()
    {
        var ct = TestContext.Current.CancellationToken;

        // A second sign-in from a different "device".
        var second = await _context.LoginAsync("ada@test.local", ApiTestContext.DefaultPassword);
        using var other = _context.ClientFor(second);

        var mine = await ListSessionsAsync(_client, ct);
        var theirs = await ListSessionsAsync(other, ct);

        Assert.Equal(2, mine.Count);
        Assert.Single(mine, s => s.IsCurrent);
        // Same two sessions, opposite answers about which one is "here".
        Assert.NotEqual(
            mine.Single(s => s.IsCurrent).Id,
            theirs.Single(s => s.IsCurrent).Id);
    }

    [Fact]
    public async Task revoking_a_session_stops_its_refresh_token_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;

        var second = await _context.LoginAsync("ada@test.local", ApiTestContext.DefaultPassword);
        using var other = _context.ClientFor(second);

        var doomed = (await ListSessionsAsync(other, ct)).Single(s => s.IsCurrent);
        var response = await _client.DeleteAsync($"/api/v1/me/sessions/{doomed.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The revoked family can no longer rotate; the surviving one still can.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(second.RefreshToken!, ct));
        Assert.Single(await ListSessionsAsync(_client, ct));
    }

    [Fact]
    public async Task a_session_id_from_another_account_is_a_404_not_a_revocation()
    {
        var ct = TestContext.Current.CancellationToken;

        var stranger = await _context.RegisterAsync("grace@test.local", "Grace", "Hopper");
        using var strangerClient = _context.ClientFor(stranger);
        var theirs = (await ListSessionsAsync(strangerClient, ct)).Single();

        var response = await _client.DeleteAsync($"/api/v1/me/sessions/{theirs.Id}", ct);

        // A family id is a uuid in a URL. Scoping by user is what stops it reaching
        // someone else's session, and a 404 is what stops it confirming one exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await ListSessionsAsync(strangerClient, ct));
    }

    [Fact]
    public async Task signing_out_everywhere_else_keeps_the_session_that_asked()
    {
        var ct = TestContext.Current.CancellationToken;

        await _context.LoginAsync("ada@test.local", ApiTestContext.DefaultPassword);
        await _context.LoginAsync("ada@test.local", ApiTestContext.DefaultPassword);
        Assert.Equal(3, (await ListSessionsAsync(_client, ct)).Count);

        var response = await _client.DeleteAsync("/api/v1/me/sessions", ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Signing out the browser that pressed the button reads as an error rather than
        // as a confirmation, so it is the one thing this endpoint leaves alone.
        var remaining = Assert.Single(await ListSessionsAsync(_client, ct));
        Assert.True(remaining.IsCurrent);
    }

    // ---------------------------------------------------------------------------- helpers

    private Task<ProfileView> GetAsync(CancellationToken ct) =>
        _client.GetFromJsonAsync<ProfileView>("/api/v1/me", ApiTestContext.Json, ct)!;

    private async Task<ProfileView> PatchAsync(UpdateProfileRequest request, CancellationToken ct)
    {
        var response = await _client.PatchAsJsonAsync("/api/v1/me", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileView>(ApiTestContext.Json, ct))!;
    }

    private async Task<AvatarUploadTicket> PresignAsync(string contentType, long length, CancellationToken ct)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/me/avatar",
            new AvatarUploadRequest(contentType, length), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AvatarUploadTicket>(ApiTestContext.Json, ct))!;
    }

    private async Task<ProfileView> CommitAsync(string key, CancellationToken ct)
    {
        var response = await _client.PutAsJsonAsync("/api/v1/me/avatar", new AvatarCommitRequest(key), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileView>(ApiTestContext.Json, ct))!;
    }

    /// <summary>The whole three-step dance, returning the key that ended up on the row.</summary>
    private async Task<string> UploadAsync(CancellationToken ct)
    {
        var ticket = await PresignAsync("image/png", 64, ct);
        await PutAsync(ticket.UploadUrl, "image/png", Png(64), ct);
        return (await CommitAsync(ticket.Key, ct)).AvatarKey!;
    }

    private async Task PutAsync(string url, string contentType, byte[] bytes, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var response = await _store.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Asked through a fresh presigned GET rather than through the API, so the answer is
    /// about the object rather than about the row that used to point at it.
    /// </summary>
    private async Task<bool> ExistsInStoreAsync(string key, CancellationToken ct)
    {
        using var scope = _context.Factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        return await storage.ExistsAsync(key, ct);
    }

    private async Task<IReadOnlyList<SessionView>> ListSessionsAsync(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<List<SessionView>>("/api/v1/me/sessions", ApiTestContext.Json, ct))!;

    private async Task<HttpStatusCode> RefreshStatusAsync(string refreshToken, CancellationToken ct)
    {
        using var anonymous = _context.Anonymous();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(refreshToken), ct);
        return response.StatusCode;
    }

    /// <summary>A real PNG header followed by filler - enough that the store sees an image.</summary>
    private static byte[] Png(int length)
    {
        var bytes = new byte[Math.Max(length, 8)];
        ReadOnlySpan<byte> header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        header.CopyTo(bytes);
        return bytes;
    }
}
