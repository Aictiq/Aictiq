using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aictiq.IntegrationTests.Auth;

/// <summary>
/// A provider that answers the two calls a real one would, without a network.
///
/// It replaces only <see cref="OAuthOptions.Backchannel"/>, so everything else in the
/// flow is the framework's real handler: the authorize redirect, the <c>state</c>
/// parameter, PKCE, the correlation cookie and the external sign-in cookie are all
/// exercised. Only the far side of the wire is ours, which is the part that cannot be
/// reached from a test anyway.
/// </summary>
public sealed class FakeOAuthProvider : HttpMessageHandler
{
    public string Subject { get; set; } = "provider-user-1";
    public string? Email { get; set; } = "ada@test.local";
    public bool EmailVerified { get; set; } = true;
    public string DisplayName { get; set; } = "Ada Lovelace";

    /// <summary>
    /// GitHub only: what happens when the <c>user:email</c> scope was declined at the
    /// consent screen. The addresses endpoint 404s and nothing verified is known.
    /// </summary>
    public bool EmailScopeDenied { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (request.Method == HttpMethod.Post)
        {
            return Task.FromResult(Json("""
                {"access_token":"fake-access-token","token_type":"Bearer","expires_in":3600}
                """));
        }

        if (url.Contains("/user/emails", StringComparison.Ordinal))
        {
            if (EmailScopeDenied)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // GitHub returns every address; only the primary *verified* one is worth
            // anything, so the fake includes decoys that must be ignored.
            return Task.FromResult(Json($$"""
                [
                  {"email":"noreply@test.local","primary":false,"verified":true},
                  {"email":{{JsonSerializer.Serialize(Email)}},"primary":true,"verified":{{(EmailVerified ? "true" : "false")}}}
                ]
                """));
        }

        if (url.Contains("api.github.com/user", StringComparison.Ordinal))
        {
            // GitHub's profile email is whatever the account made public — deliberately a
            // different, unverified address here, so a test fails if it is ever trusted.
            return Task.FromResult(Json($$"""
                {"id":{{JsonSerializer.Serialize(Subject)}},"login":"ada",
                 "name":{{JsonSerializer.Serialize(DisplayName)}},
                 "email":"public-but-unverified@test.local"}
                """));
        }

        return Task.FromResult(Json($$"""
            {"sub":{{JsonSerializer.Serialize(Subject)}},
             "email":{{JsonSerializer.Serialize(Email)}},
             "email_verified":{{(EmailVerified ? "true" : "false")}},
             "name":{{JsonSerializer.Serialize(DisplayName)}}}
            """));
    }

    /// <summary>
    /// Swaps this in for both providers' back channel. Registered after the framework's own
    /// post-configure, so it wins.
    /// </summary>
    public void Install(IServiceCollection services)
    {
        var client = new HttpClient(this) { Timeout = TimeSpan.FromSeconds(5) };
        services.AddSingleton<IPostConfigureOptions<OAuthOptions>>(
            new UseFakeBackchannel(client));
    }

    private sealed class UseFakeBackchannel(HttpClient client) : IPostConfigureOptions<OAuthOptions>
    {
        public void PostConfigure(string? name, OAuthOptions options) => options.Backchannel = client;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
