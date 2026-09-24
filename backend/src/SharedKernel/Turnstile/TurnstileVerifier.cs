using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aictiq.SharedKernel.Turnstile;

public enum TurnstileOutcome
{
    /// <summary>Cloudflare vouched for this token, for this action, on an allowed host.</summary>
    Passed,

    /// <summary>No token, a malformed one, or one Cloudflare refused. The visitor solves it again.</summary>
    Failed,

    /// <summary>
    /// Cloudflare could not be asked. Refused all the same - fail closed - because an
    /// outage that switched the check off would be exactly the window a bot waits for.
    /// </summary>
    Unavailable,
}

public interface ITurnstileVerifier
{
    bool IsEnabled { get; }

    /// <param name="action">
    /// What the widget was rendered for (<c>login</c>, <c>register</c>...). A token solved on
    /// the password-reset form must not open the sign-up form.
    /// </param>
    Task<TurnstileOutcome> VerifyAsync(string? token, string action, CancellationToken cancellationToken);
}

/// <summary>
/// Server-side validation of a Turnstile token against Cloudflare's siteverify endpoint.
///
/// The widget on its own proves nothing: it runs in the attacker's browser, and a script
/// that never loads it simply posts the form. What makes the challenge a control is this
/// call - the token is single-use, expires after five minutes, and only Cloudflare can say
/// whether it was earned. Every token is therefore checked here, once, on the request that
/// carries it.
/// </summary>
/// <remarks>
/// The visitor's IP is deliberately not sent. It is optional, and behind Cloudflare, a
/// proxy and Caddy the address the API sees depends on how many hops are trusted - a
/// wrong one is a worse signal than none.
/// </remarks>
public sealed class TurnstileVerifier(
    HttpClient http,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    public const string HttpClientName = "Turnstile";

    /// <summary>Cloudflare's documented ceiling for a token.</summary>
    public const int MaxTokenLength = 2048;

    public bool IsEnabled => options.Value.IsEnabled;

    public async Task<TurnstileOutcome> VerifyAsync(
        string? token, string action, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsEnabled)
        {
            return TurnstileOutcome.Passed;
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return TurnstileOutcome.Failed;
        }

        SiteVerifyResponse? result;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = settings.SecretKey!,
                ["response"] = token,
            });
            using var response = await http.PostAsync(settings.VerifyUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile siteverify answered {StatusCode}", (int)response.StatusCode);
                return TurnstileOutcome.Unavailable;
            }

            result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
        }
        catch (Exception ex) when (
            (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Turnstile siteverify could not be reached");
            return TurnstileOutcome.Unavailable;
        }

        if (result is null)
        {
            return TurnstileOutcome.Unavailable;
        }

        if (!result.Success)
        {
            // invalid-input-secret is ours to fix, not the visitor's: say so loudly, once
            // per request, and still refuse.
            if (result.ErrorCodes.Contains("invalid-input-secret", StringComparer.Ordinal))
            {
                logger.LogError("Turnstile rejected the configured secret key (Turnstile:SecretKey)");
                return TurnstileOutcome.Unavailable;
            }

            logger.LogInformation("Turnstile refused a token for {Action}: {Errors}",
                action, string.Join(",", result.ErrorCodes));
            return TurnstileOutcome.Failed;
        }

        // Every widget this SPA renders names its action, and only hosts on the site key's
        // list can render one, so a real token always carries it. An empty action comes
        // from Cloudflare's dummy test keys, which are for exercising a deployment - they
        // are accepted so staging can be tested without a live challenge.
        if (!string.IsNullOrEmpty(result.Action)
            && !string.Equals(result.Action, action, StringComparison.Ordinal))
        {
            logger.LogInformation("Turnstile token for action {Actual} presented to {Expected}",
                result.Action, action);
            return TurnstileOutcome.Failed;
        }

        // Blank entries are ignored: compose binds an unset variable to "", and a list
        // holding only that would otherwise refuse every token from every host.
        var allowed = settings.AllowedHostnames.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        if (allowed.Length > 0
            && !allowed.Contains(result.Hostname ?? "", StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation("Turnstile token solved on unexpected host {Hostname}", result.Hostname);
            return TurnstileOutcome.Failed;
        }

        return TurnstileOutcome.Passed;
    }

    private sealed class SiteVerifyResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("error-codes")]
        public string[] ErrorCodes { get; init; } = [];

        [JsonPropertyName("hostname")]
        public string? Hostname { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }
    }
}
