using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aictiq.Modules.Identity.External;

/// <summary>
/// Google and GitHub sign-in, on the framework's own <c>AddOAuth</c>.
///
/// Deliberately not the per-provider packages. Both providers need custom work in
/// <c>OnCreatingTicket</c> anyway - the rule that an unverified provider email is worth
/// nothing means reading <c>email_verified</c> from Google and calling GitHub's
/// <c>/user/emails</c> - so the packages would save no code while adding two dependencies.
/// Standing on <c>AddOAuth</c> also makes the flow testable: an integration test replaces
/// <see cref="OAuthOptions.Backchannel"/> with a stub provider and drives the real handler
/// end to end, state and correlation cookie included.
/// </summary>
public static class ExternalAuthExtensions
{
    /// <summary>Where the OAuth handlers themselves intercept the provider's redirect.</summary>
    public static string CallbackPath(string provider) => $"/api/v1/auth/external/{provider}/oauth";

    /// <summary>Where the handler sends the browser afterwards - our own endpoint.</summary>
    public static string CompletionPath(string provider) => $"/api/v1/auth/external/{provider}/callback";

    public static AuthenticationBuilder AddExternalAuth(
        this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var options = configuration.GetSection(ExternalAuthOptions.SectionName).Get<ExternalAuthOptions>()
            ?? new ExternalAuthOptions();

        builder.Services.Configure<ExternalAuthOptions>(configuration.GetSection(ExternalAuthOptions.SectionName));

        var configured = options.Configured().ToList();
        if (configured.Count == 0)
        {
            // Nothing to register. The cookie scheme below only exists to carry an OAuth
            // result, so without a provider it would be a scheme nothing can sign into.
            return builder;
        }

        builder.AddCookie(ExternalProviders.ExternalScheme, cookie =>
        {
            // Seconds, not a session: this cookie exists between the provider's redirect
            // and the callback reading it, and the callback signs it out immediately.
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.IsEssential = true;
            // Lax, because the provider's redirect back is a top-level GET navigation from
            // their origin. Strict would drop the cookie on exactly the request it exists for.
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        foreach (var (name, provider) in configured)
        {
            if (name == ExternalProviders.Google)
            {
                builder.AddOAuth(name, ConfigureGoogle(provider));
            }
            else
            {
                builder.AddOAuth(name, ConfigureGitHub(provider));
            }
        }

        return builder;
    }

    private static Action<OAuthOptions> ConfigureGoogle(ExternalProviderOptions provider) => options =>
    {
        Common(options, provider, ExternalProviders.Google);

        options.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        options.TokenEndpoint = "https://oauth2.googleapis.com/token";
        options.UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";

        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");

        options.Events.OnCreatingTicket = async context =>
        {
            using var payload = await GetJsonAsync(context, options.UserInformationEndpoint);
            var root = payload.RootElement;

            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            AddClaim(identity, ExternalIdentity.ProviderKeyClaim, Text(root, "sub"));
            AddClaim(identity, ExternalIdentity.EmailClaim, Text(root, "email"));
            // Google states it outright; anything but a literal true is treated as false.
            AddClaim(identity, ExternalIdentity.EmailVerifiedClaim,
                root.TryGetProperty("email_verified", out var verified)
                    && verified.ValueKind == JsonValueKind.True ? "true" : "false");

            var (first, last) = ExternalIdentity.SplitName(Text(root, "name"), Text(root, "email"));
            AddClaim(identity, ExternalIdentity.FirstNameClaim, Text(root, "given_name") ?? first);
            AddClaim(identity, ExternalIdentity.LastNameClaim, Text(root, "family_name") ?? last);
        };
    };

    private static Action<OAuthOptions> ConfigureGitHub(ExternalProviderOptions provider) => options =>
    {
        Common(options, provider, ExternalProviders.GitHub);

        options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
        options.TokenEndpoint = "https://github.com/login/oauth/access_token";
        options.UserInformationEndpoint = "https://api.github.com/user";

        options.Scope.Add("read:user");
        // Without this scope GitHub returns only the public profile email, which may be
        // absent, stale, or one the account never proved it owns.
        options.Scope.Add("user:email");

        options.Events.OnCreatingTicket = async context =>
        {
            using var payload = await GetJsonAsync(context, options.UserInformationEndpoint);
            var root = payload.RootElement;

            var identity = (ClaimsIdentity)context.Principal!.Identity!;
            AddClaim(identity, ExternalIdentity.ProviderKeyClaim,
                root.TryGetProperty("id", out var id) ? id.ToString() : null);

            // GitHub's profile email is whatever the account chose to make public, which
            // is not the same as verified. The addresses endpoint is the only source that
            // says so, and only its primary verified entry is worth anything here.
            var (email, verified) = await PrimaryVerifiedEmailAsync(context);
            AddClaim(identity, ExternalIdentity.EmailClaim, email);
            AddClaim(identity, ExternalIdentity.EmailVerifiedClaim, verified ? "true" : "false");

            var (first, last) = ExternalIdentity.SplitName(
                Text(root, "name") ?? Text(root, "login"), email);
            AddClaim(identity, ExternalIdentity.FirstNameClaim, first);
            AddClaim(identity, ExternalIdentity.LastNameClaim, last);
        };
    };

    private static void Common(OAuthOptions options, ExternalProviderOptions provider, string name)
    {
        options.ClientId = provider.ClientId!;
        options.ClientSecret = provider.ClientSecret!;
        options.CallbackPath = CallbackPath(name);

        // The OAuth result lands in a short-lived cookie rather than in the API's own
        // session: the callback endpoint decides which Aictiq account this is before any
        // credential of ours is issued.
        options.SignInScheme = ExternalProviders.ExternalScheme;

        // PKCE and the state parameter are the framework's, not ours - the handler
        // generates the verifier, and state carries the correlation id plus the properties
        // (`next`, an invitation token, a link target) that our endpoints put there.
        options.UsePkce = true;
        options.SaveTokens = false;

        // Scoped to the callback path by the handler, so it is not attached to any other
        // request. SameAsRequest rather than Always: a development instance is http, and a
        // Secure cookie the browser refuses to send back would break the flow entirely.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        // Denied consent, a tampered state, a correlation cookie that never arrived: all
        // of them are the handler refusing, and all of them belong on the login page. The
        // default rethrows, which would show a person a 500 for pressing "Cancel".
        options.Events.OnRemoteFailure = context =>
        {
            context.Response.Redirect("/login?error=provider-failed");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static async Task<JsonDocument> GetJsonAsync(OAuthCreatingTicketContext context, string endpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        // GitHub rejects API calls without one; harmless everywhere else.
        request.Headers.UserAgent.ParseAdd("Aictiq");

        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
    }

    private static async Task<(string? Email, bool Verified)> PrimaryVerifiedEmailAsync(
        OAuthCreatingTicketContext context)
    {
        try
        {
            using var payload = await GetJsonAsync(context, "https://api.github.com/user/emails");
            foreach (var entry in payload.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True
                    && entry.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True)
                {
                    return (Text(entry, "email"), true);
                }
            }
        }
        catch (HttpRequestException)
        {
            // The scope can be declined at the consent screen. Then we simply do not know
            // an address we can trust, and the caller turns that into a refusal rather
            // than falling back to the public profile email.
        }

        return (null, false);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AddClaim(ClaimsIdentity identity, string type, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            identity.AddClaim(new Claim(type, value));
        }
    }
}
