using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.SharedKernel.Http;

/// <summary>
/// A small post-authentication limiter for expensive operations. ASP.NET's global
/// limiter runs before authentication, which is exactly right for IP/PAT abuse but means
/// it cannot safely use the authenticated user or resolved tenant as a partition key.
/// Endpoint filters run after both are established and add that second budget.
/// </summary>
public sealed class OperationRateLimiter(IConfiguration configuration, TimeProvider clock)
{
    public const string Uploads = "uploads";
    public const string Search = "search";

    private readonly int _uploadLimit = Positive(configuration, "RateLimiting:UploadPermitLimitPerMinute", 20);
    private readonly int _searchLimit = Positive(configuration, "RateLimiting:SearchPermitLimitPerMinute", 120);
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private long _requests;

    /// <returns>The number of seconds until the caller may retry, or <see langword="null"/> when admitted.</returns>
    public int? RetryAfterSeconds(string operation, string? userId, Guid? organizationId, string? fallbackAddress)
    {
        var now = clock.GetUtcNow();
        // A protected endpoint has an authenticated user. Keep the fallback fail-closed
        // for a future endpoint accidentally wired before authentication instead of
        // putting every anonymous caller in one shared bucket.
        var subject = string.IsNullOrWhiteSpace(userId) ? $"ip:{fallbackAddress ?? "unknown"}" : $"user:{userId}";
        var tenant = organizationId is { } org ? $"org:{org:N}" : "org:none";
        var key = $"{operation}:{tenant}:{subject}";
        var window = _windows.GetOrAdd(key, _ => new Window(now));

        if (Interlocked.Increment(ref _requests) % 128 == 0)
        {
            foreach (var (candidate, value) in _windows)
            {
                if (now - value.Start > TimeSpan.FromMinutes(2)) _windows.TryRemove(candidate, out _);
            }
        }

        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Calls = 0;
            }

            if (window.Calls++ < Limit(operation)) return null;
            return Math.Max(1, (int)Math.Ceiling((window.Start.AddMinutes(1) - now).TotalSeconds));
        }
    }

    private int Limit(string operation) => operation switch
    {
        Uploads => _uploadLimit,
        Search => _searchLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation rate-limit policy.")
    };

    private static int Positive(IConfiguration configuration, string key, int fallback) =>
        Math.Max(1, configuration.GetValue(key, fallback));

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Calls;
    }
}

/// <summary>Applies an authenticated organization/user operation budget to a route handler.</summary>
public static class OperationRateLimitingEndpointExtensions
{
    public static RouteHandlerBuilder RequireOperationRateLimit(this RouteHandlerBuilder builder, string operation)
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();
            var tenant = http.RequestServices.GetRequiredService<ICurrentTenant>();
            var limiter = http.RequestServices.GetRequiredService<OperationRateLimiter>();
            var retryAfter = limiter.RetryAfterSeconds(operation, currentUser.UserId, tenant.OrganizationId,
                http.Connection.RemoteIpAddress?.ToString());
            if (retryAfter is null) return await next(context);

            http.Response.Headers.RetryAfter = retryAfter.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(title: "Rate limit exceeded.", detail: "Retry after the indicated number of seconds.",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = retryAfter.Value });
        });
        return builder;
    }
}
