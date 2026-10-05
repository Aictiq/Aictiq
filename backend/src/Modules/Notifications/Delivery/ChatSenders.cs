using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.Modules.Notifications.Domain;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Notifications.Delivery;

/// <summary>
/// A send that did not go through. The message is written for the person who owns the
/// channel and is safe to store and show: it never contains the webhook URL or bot token.
/// </summary>
public sealed class ChatDeliveryException(string message) : Exception(message);

public interface IChatSender
{
    ChatChannelType Type { get; }

    /// <summary>Posts already formatted text to the decrypted target.</summary>
    Task SendAsync(string target, string text, CancellationToken cancellationToken);
}

/// <summary>Shared by the three platforms: one HTTP client that never logs or traces its URLs.</summary>
public abstract class ChatSender(IHttpClientFactory clients)
{
    /// <summary>
    /// Registered with its logging handlers removed, and excluded from HTTP client tracing
    /// by <see cref="SecretUrlOption"/>: a Slack or Discord webhook URL, or a Telegram API
    /// path, <em>is</em> the credential.
    /// </summary>
    public const string HttpClientName = "aictiq-chat";

    /// <summary>Marks a request whose URL must not be recorded; ServiceDefaults filters on it.</summary>
    public static readonly HttpRequestOptionsKey<bool> SecretUrlOption = new("aictiq.secret-url");

    protected async Task PostAsync(string url, object body, Func<HttpStatusCode, string, string> describe, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Options.Set(SecretUrlOption, true);
        HttpResponseMessage response;
        try { response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken); }
        catch (HttpRequestException) { throw new ChatDeliveryException("The platform could not be reached."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ChatDeliveryException("The platform did not answer in time."); }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ChatDeliveryException(describe(response.StatusCode, content.Length > 300 ? content[..300] : content));
        }
    }
}

public sealed class SlackSender(IHttpClientFactory clients) : ChatSender(clients), IChatSender
{
    public ChatChannelType Type => ChatChannelType.Slack;

    public Task SendAsync(string target, string text, CancellationToken cancellationToken) =>
        PostAsync(target, new { text, unfurl_links = false }, (status, body) => status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden =>
                $"Slack refused the webhook ({(int)status}{(body.Length > 0 ? $" {Word(body)}" : "")}): it may have been removed or its channel archived.",
            HttpStatusCode.TooManyRequests => "Slack is rate limiting this webhook.",
            _ => $"Slack answered {(int)status}."
        }, cancellationToken);

    /// <summary>Slack's error bodies are a single code such as <c>no_service</c>; anything else is not echoed.</summary>
    private static string Word(string body) => body.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && body.Length <= 40 ? body : "";
}

public sealed class DiscordSender(IHttpClientFactory clients) : ChatSender(clients), IChatSender
{
    public ChatChannelType Type => ChatChannelType.Discord;

    public Task SendAsync(string target, string text, CancellationToken cancellationToken) =>
        PostAsync(target, new { content = text, allowed_mentions = new { parse = Array.Empty<string>() } }, (status, _) => status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"Discord refused the webhook ({(int)status}): it may have been deleted.",
            HttpStatusCode.TooManyRequests => "Discord is rate limiting this webhook.",
            _ => $"Discord answered {(int)status}."
        }, cancellationToken);
}

public sealed class TelegramSender(IHttpClientFactory clients, IOptions<TelegramOptions> options) : ChatSender(clients), IChatSender
{
    public ChatChannelType Type => ChatChannelType.Telegram;

    public Task SendAsync(string target, string text, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) throw new ChatDeliveryException("Telegram is not configured on this instance.");
        return PostAsync($"https://api.telegram.org/bot{options.Value.BotToken}/sendMessage",
            new { chat_id = target, text, parse_mode = "HTML", disable_web_page_preview = true },
            (status, body) => status switch
            {
                HttpStatusCode.Forbidden => "The Aictiq bot was blocked or removed from the chat.",
                HttpStatusCode.TooManyRequests => "Telegram is rate limiting the bot.",
                _ => $"Telegram answered {(int)status}{Description(body)}."
            }, cancellationToken);
    }

    /// <summary>Telegram's own explanation, e.g. "Bad Request: chat not found". It names no secrets.</summary>
    private static string Description(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("description", out var description) && description.GetString() is { Length: <= 200 } text
                ? $": {text}" : "";
        }
        catch (JsonException) { return ""; }
    }
}
