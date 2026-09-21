using Microsoft.AspNetCore.Http;

namespace Aictiq.SharedKernel.Authorization;

/// <summary>
/// "A bearer value starting with <see cref="Prefix"/> belongs to <see cref="Scheme"/>."
///
/// The default authentication scheme (Identity's policy scheme) forwards a request by the
/// shape of its bearer value. A module that brings a credential of its own registers one of
/// these in DI instead of teaching Identity about it — which is how a runner's <c>jrn_</c>
/// secret reaches Automation's handler while Identity never learns what a runner is.
/// </summary>
public sealed record BearerCredentialRoute(string Prefix, string Scheme)
{
    /// <summary>The raw bearer value of the request, or null when there is none.</summary>
    public static string? ReadBearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header))
        {
            return null;
        }

        var value = header.ToString();
        const string bearer = "Bearer ";
        return value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? value[bearer.Length..].Trim()
            : null;
    }
}
