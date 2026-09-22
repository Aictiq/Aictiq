using System.Collections.Concurrent;
using System.Globalization;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;

namespace Aictiq.Api.Mcp;

/// <summary>
/// Limits tool calls, rather than HTTP messages, so an MCP client receives a normal tool
/// error that tells it exactly when it may retry. The HTTP limiter still protects the
/// transport from malformed-message floods; this limiter protects the useful work.
/// </summary>
public sealed class McpToolRateLimiter(IConfiguration configuration, TimeProvider clock)
{
    private readonly int _permitLimit = Math.Max(1, configuration.GetValue("RateLimiting:McpPermitLimitPerMinute", 60));
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <returns>The whole number of seconds to wait, or <see langword="null"/> when admitted.</returns>
    public int? RetryAfterSeconds(HttpRequest request)
    {
        // The endpoint policy already requires a Aictiq PAT. Hashing keeps both the in-memory
        // partition key and every diagnostic free of the bearer secret.
        var key = PatDefaults.ReadToken(request) is { } token
            ? PersonalAccessToken.Hash(token)[..16]
            : "anonymous";
        var now = clock.GetUtcNow();
        var window = _windows.GetOrAdd(key, _ => new Window(now));

        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Calls = 0;
            }

            if (window.Calls++ < _permitLimit)
            {
                return null;
            }

            return Math.Max(1, (int)Math.Ceiling((window.Start.AddMinutes(1) - now).TotalSeconds));
        }
    }

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Calls;
    }
}
