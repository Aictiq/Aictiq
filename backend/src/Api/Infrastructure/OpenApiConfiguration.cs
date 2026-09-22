using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aictiq.Api.Infrastructure;

/// <summary>
/// Keeps the public contract useful even where a minimal-API handler returns IResult.
/// Endpoint tags remain owned by their modules; this common pass supplies the metadata
/// every consumer needs to navigate the document consistently.
/// </summary>
public static class OpenApiConfiguration
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Aictiq REST API",
                Version = "v1",
                Description = "Version 1 of Aictiq's public REST API. See /docs/api.md for authentication, filters, pagination, concurrency, limits, errors, and the compatibility policy."
            };

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "aictiq.at",
                Description = "Browser session cookie. State-changing requests also require the X-Aictiq-Request: 1 header."
            };
            document.Components.SecuritySchemes["personalAccessToken"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "Aictiq personal access token",
                Description = "Personal access token. Send Authorization: Bearer <token>. Token scopes and organization binding apply."
            };

            return Task.CompletedTask;
        });

        options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var method = context.Description.HttpMethod?.ToUpperInvariant() ?? "CALL";
            var path = context.Description.RelativePath?.Split('?', 2)[0] ?? "api";

            operation.OperationId ??= BuildOperationId(method, path);
            operation.Summary ??= Describe(method, path);
            operation.Description ??= "Use the documented request and response shapes. Error responses use RFC 9457 problem details and include a traceId for support.";

            foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
            {
                parameter.Example ??= ExampleFor(parameter.Name ?? "parameter");
            }

            var problemSchema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null!, cancellationToken);
            AddProblemResponse(operation, "400", "The request is invalid.", 400, "Invalid request", problemSchema);
            AddProblemResponse(operation, "401", "Authentication is required or expired.", 401, "Unauthorized", problemSchema);
            AddProblemResponse(operation, "403", "The credential lacks the required organization role or token scope.", 403, "Forbidden", problemSchema);
            AddProblemResponse(operation, "404", "The requested resource does not exist or is not visible to this caller.", 404, "Not found", problemSchema);
            AddProblemResponse(operation, "409", "The resource changed since the supplied version, or a domain conflict exists.", 409, "Conflict", problemSchema);
            AddProblemResponse(operation, "429", "The rate limit was exceeded; retry after the current one-minute window.", 429, "Too many requests", problemSchema);
        });
    }

    private static void AddProblemResponse(OpenApiOperation operation, string status, string description, int code, string title, IOpenApiSchema problemSchema)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.ContainsKey(status))
            return;

        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = problemSchema,
                    Example = new JsonObject
                    {
                        ["type"] = $"https://aictiq.com/problems/{title.ToLowerInvariant().Replace(' ', '-')}",
                        ["title"] = title,
                        ["status"] = code,
                        ["traceId"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00"
                    }
                }
            }
        };
    }

    private static JsonNode ExampleFor(string name) => name switch
    {
        "orgSlug" => "acme",
        "projectKey" => "AICTIQ",
        "itemKey" => "ACME-123",
        "provider" => "github",
        "emoji" => "thumbsup",
        "token" => "invitation-token",
        _ when name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) => "11111111-1111-1111-1111-111111111111",
        _ => "example"
    };

    private static string BuildOperationId(string method, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.Equals(part, "api", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(part, "v1", StringComparison.OrdinalIgnoreCase))
            .Select(part => part.StartsWith('{') ? $"By{part.Trim('{', '}').ToPascalCase()}" : part.ToPascalCase());

        return $"{method.ToLowerInvariant()}{string.Concat(parts)}";
    }

    private static string Describe(string method, string path) => $"{method} {path}";

    private static string ToPascalCase(this string value) => string.Concat(value
        .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
