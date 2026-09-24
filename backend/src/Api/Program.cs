using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Aictiq.Api.Infrastructure;
using Aictiq.Api.Seeding;
using Aictiq.Api.Realtime;
using Aictiq.Api.Mcp;
using Aictiq.Modules.Identity;
using Aictiq.Modules.Integrations;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Domain;
using Aictiq.Modules.Identity.External;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.Wiki;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Billing;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Auth;
using Aictiq.Modules.Automation.Domain;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Contracts;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Mcp;
using Aictiq.SharedKernel.Realtime;
using Aictiq.SharedKernel.Authorization;
using Aictiq.SharedKernel.Tenancy;
using Aictiq.SharedKernel.Turnstile;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);
var runDemoSeeder = args.Any(arg => string.Equals(arg, "aictiq-seed", StringComparison.OrdinalIgnoreCase));
var runPerfSeeder = args.Any(arg => string.Equals(arg, "aictiq-seed-perf", StringComparison.OrdinalIgnoreCase));

builder.AddServiceDefaults();

builder.AddNpgsqlDataSource("appdb");

builder.Services.AddSharedKernel();
// Disabled by default. When an operator explicitly enables it, this background service
// sends only a version and aggregate counts; it never bypasses the runtime RLS role.
builder.Services.AddHttpClient("AictiqTelemetry", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHostedService<AnonymousTelemetryReporter>();
builder.Services.AddBlobStorage(builder.Configuration);
// Optional: with no Email:Smtp:Host the API still starts and reports the fact on
// /health/ready, so invitations can offer a copyable link instead.
builder.Services.AddEmail(builder.Configuration);
// Optional: with no Turnstile keys the anonymous forms are not challenged, which is what
// development and a private self-hosted instance want. A public deployment sets both.
builder.Services.AddTurnstile(builder.Configuration);
builder.Services.AddHybridCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddTenancyModule();
builder.Services.AddBillingModule(builder.Configuration);
builder.Services.AddWorkItemsModule();
builder.Services.AddWikiModule();
builder.Services.AddIntegrationsModule(builder.Configuration);
builder.Services.AddAnalyticsModule(builder.Configuration);
builder.Services.AddAutomationModule(builder.Configuration);
builder.Services.AddSingleton<IMcpToolProvider>(new McpToolProvider(typeof(McpContextTools).Assembly));
builder.Services.AddScoped<McpToolAuditService>();
builder.Services.AddSingleton<McpToolRateLimiter>();
// The API owns notify's migrations; the queue itself is drained by Workers.
builder.Services.AddNotificationsModule();
// Membership and organization lookups are cached on the request-authorization path, so
// every instance has to hear about a change, not just the one that made it.
builder.Services.AddHostedService<TenancyCacheInvalidator>();

// Realtime is a host concern: domain modules publish the SharedKernel contract and the
// API owns the transport. Postgres is the zero-extra-service default; Redis uses
// SignalR's supported scale-out provider; none is useful for one-off/offline hosts.
builder.Services.AddOptions<RealtimeOptions>()
    .Bind(builder.Configuration.GetSection(RealtimeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var realtime = builder.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
// Detailed errors carry exception text to the browser, so they are a development-only
// affordance: without them a hub that throws on connect is indistinguishable at the
// client from a network fault, which is exactly the failure that is hardest to chase.
var signalR = builder.Services.AddSignalR(options =>
    options.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.RemoveAll<IRealtimePublisher>();
builder.Services.RemoveAll<IUserRealtimePublisher>();
builder.Services.RemoveAll<IRunRealtimePublisher>();
switch (realtime.Backplane.ToLowerInvariant())
{
    case "postgres":
        builder.Services.AddSingleton<PostgresRealtimePublisher>();
        builder.Services.AddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        builder.Services.AddSingleton<IUserRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        builder.Services.AddSingleton<IRunRealtimePublisher>(sp => sp.GetRequiredService<PostgresRealtimePublisher>());
        builder.Services.AddHostedService<RealtimeBackplaneListener>();
        break;
    case "redis":
        if (string.IsNullOrWhiteSpace(realtime.RedisConnectionString))
            throw new InvalidOperationException("Realtime:RedisConnectionString is required when Realtime:Backplane is redis.");
        signalR.AddStackExchangeRedis(realtime.RedisConnectionString);
        builder.Services.AddSingleton<SignalRRealtimePublisher>();
        builder.Services.AddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<SignalRRealtimePublisher>());
        builder.Services.AddSingleton<IUserRealtimePublisher>(sp => sp.GetRequiredService<SignalRRealtimePublisher>());
        builder.Services.AddSingleton<IRunRealtimePublisher>(sp => sp.GetRequiredService<SignalRRealtimePublisher>());
        // Workers have no hub: what they publish arrives over NOTIFY, and one replica
        // forwards it into the Redis backplane.
        builder.Services.AddHostedService<RealtimeBackplaneListener>();
        break;
    case "none":
        builder.Services.AddSingleton<NullRealtimePublisher>();
        builder.Services.AddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<NullRealtimePublisher>());
        builder.Services.AddSingleton<IUserRealtimePublisher>(sp => sp.GetRequiredService<NullRealtimePublisher>());
        builder.Services.AddSingleton<IRunRealtimePublisher>(sp => sp.GetRequiredService<NullRealtimePublisher>());
        break;
    default:
        throw new InvalidOperationException("Realtime:Backplane must be postgres, redis, or none.");
}
builder.Services.AddScoped<IDomainEventHandler<RealtimeCommentAdded>, CommentAddedRealtimeHandler>();
builder.Services.AddScoped<IDomainEventHandler<BoardMoved>, BoardMovedRealtimeHandler>();
builder.Services.AddScoped<IDomainEventHandler<SprintChanged>, SprintChangedRealtimeHandler>();

// JWT bearer authentication (ASP.NET Identity issues the credentials)
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration is missing.");
if (jwtOptions.Key.Length < 32)
{
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
}
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// The default scheme authenticates nothing itself: it looks at the bearer value and
// forwards to the PAT handler or to JWT, so one Authorization header carries either kind
// of credential and no endpoint has to know which.
var authentication = builder.Services.AddAuthentication(PatDefaults.PolicyScheme);

authentication.AddPersonalAccessTokens();
// A factory runner's jrn_ secret: its own handler, reachable only on /runner/*.
authentication.AddRunnerCredentials();

// Google and GitHub, each registered only when its credentials are configured. The
// default scheme stays JWT, so [Authorize] is unaffected - these are challenged by name.
authentication.AddExternalAuth(builder.Configuration);

authentication
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        // Keep the short JWT claim names ("sub", "name", "role") instead of remapping
        // them to the legacy ClaimTypes.* URIs - clients and ICurrentUser read the same names.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // The SPA has no Authorization header to send - its access token is an httpOnly
        // cookie it cannot read. Fall back to that cookie, and remember that we did:
        // cookie-authenticated requests are the only ones a cross-site page could
        // trigger, so they are the only ones that need the CSRF header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token)
                    && !context.Request.Headers.ContainsKey("Authorization")
                    && context.Request.Cookies.TryGetValue(AuthCookies.AccessCookieName, out var cookieToken)
                    && !string.IsNullOrEmpty(cookieToken))
                {
                    context.Token = cookieToken;
                    context.HttpContext.Items[AuthCookies.CookieAuthenticatedItemKey] = true;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));
    // MCP is deliberately PAT-only. Unlike ordinary scope checks, absence of scopes is
    // not permissive here: a browser or a broad PAT must never become an agent endpoint.
    options.AddPolicy(PatDefaults.Scheme, p => p
        .AddAuthenticationSchemes(PatDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(PrincipalClaims.Scope, Scopes.Mcp)
        .RequireClaim(PrincipalClaims.Organization));
});

// Enums cross the wire as camelCase names, never as their numbers. This API is also the
// CLI's and the MCP server's surface, and `"role": 0` is not something an agent - or a
// person reading a log - can act on. It costs a rename to change a member's number; it
// costs a breaking change to move from numbers to names later, so the names go on now,
// at the first enum to be exposed.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// problem+json for every error path (RFC 9457)
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Behind a reverse proxy the client address arrives in X-Forwarded-For; without this
// the per-IP rate limiter throttles the proxy itself.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Clear();
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }
});

// Rate limiting: global per-IP limit plus a stricter policy for auth endpoints.
var globalPermitLimit = builder.Configuration.GetValue("RateLimiting:GlobalPermitLimitPerMinute", 300);
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitLimitPerMinute", 10);
var mcpRequestPermitLimit = builder.Configuration.GetValue("RateLimiting:McpRequestPermitLimitPerMinute", 600);
var mcpMaxRequestBodyBytes = builder.Configuration.GetValue("Mcp:MaxRequestBodyBytes", 1_048_576);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            // A personal access token gets its own budget, keyed by the token rather than
            // by the address it came from: agents and CI share an egress IP, and one busy
            // token must not throttle the rest. Read straight from the header, because the
            // limiter runs before authentication - and hashed, so the key is not a secret.
            partitionKey: PatDefaults.ReadToken(context.Request) is { } token
                ? $"pat:{PersonalAccessToken.Hash(token)[..16]}"
                // A runner gets its budget the same way: several runners can share one
                // host's address, and one busy runner must not throttle its neighbours.
                : RunnerDefaults.ReadToken(context.Request) is { } runner
                    ? $"runner:{RunnerCredential.Hash(runner)[..16]}"
                    : context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = globalPermitLimit,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"auth_{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("mcp", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: PatDefaults.ReadToken(context.Request) is { } token
                ? $"mcp:{PersonalAccessToken.Hash(token)[..16]}"
                : "mcp:anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                // This is a coarse transport guard. Tool calls have a separate, lower
                // per-token budget below so clients receive a protocol tool error with a
                // Retry-After value instead of an opaque HTTP 429.
                PermitLimit = mcpRequestPermitLimit,
                Window = TimeSpan.FromMinutes(1)
            }));
});

var mcp = builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation { Name = "aictiq", Version = "1" };
    options.ServerInstructions = "Aictiq work-item keys look like ACME-123. Use the documented filter grammar when filtering items, and claim an item before working on it.";
})
    .WithHttpTransport(options => options.Stateless = true)
    .WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var services = context.Services!;
            // ReadOnlyHint comes from the MCP tool declaration, not a second hand-maintained
            // name list. A newly added mutating tool consequently defaults to write scope.
            var readOnly = (context.MatchedPrimitive as ModelContextProtocol.Server.McpServerTool)
                ?.ProtocolTool.Annotations?.ReadOnlyHint == true;
            var requiredScope = context.Params.Name == "whoami" ? null : readOnly ? Scopes.Read : Scopes.Write;
            await McpRequestGate.AuthorizeAsync(services, requiredScope, cancellationToken);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var outcome = "success";
            try
            {
                var result = await next(context, cancellationToken);
                // A tool that answers in words throws McpAnswerException instead of returning a
                // value; the catch below turns that into text. This guard is the last line for
                // anything that still returns null: the SDK serializes it as a result with zero
                // content blocks, which an agent cannot tell apart from a transport fault.
                if (result.Content.Count == 0)
                {
                    // Replace rather than Add: a tool is free to hand back a result whose
                    // Content is a fixed-size or read-only list, and a guard that throws would
                    // turn a silent answer into a genuine fault.
                    result.Content = [new TextContentBlock { Text = "not found or no access" }];
                }
                outcome = result.IsError == true ? "error" : "success";
                return result;
            }
            catch (McpAnswerException answer)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = answer.Message }] };
            }
            catch
            {
                outcome = "exception";
                throw;
            }
            finally
            {
                await McpRequestGate.RecordAsync(services, context.Params.Name, outcome, stopwatch, cancellationToken);
            }
        });

        // Resources read the same rows the read tools do, so they pass the same gate: a
        // token without `read`, or whose identity left the organization, is refused here
        // rather than trusted to each resource's own lookup.
        filters.AddReadResourceFilter(next => async (context, cancellationToken) =>
        {
            var services = context.Services!;
            await McpRequestGate.AuthorizeAsync(services, Scopes.Read, cancellationToken);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var outcome = "success";
            try
            {
                return await next(context, cancellationToken);
            }
            catch
            {
                outcome = "exception";
                throw;
            }
            finally
            {
                await McpRequestGate.RecordAsync(services, $"resource:{context.Params.Uri}", outcome, stopwatch, cancellationToken);
            }
        });
    });

foreach (var provider in builder.Services
    .Where(d => d.ServiceType == typeof(IMcpToolProvider))
    .Select(d => d.ImplementationInstance)
    .OfType<IMcpToolProvider>()
    .ToList())
{
    mcp.WithToolsFromAssembly(provider.ToolAssembly);
    mcp.WithResourcesFromAssembly(provider.ToolAssembly);
    mcp.WithPromptsFromAssembly(provider.ToolAssembly);
}

// No CORS: the SPA is served from this origin, and non-browser clients (CLI, MCP) are
// not subject to it. Adding an allowlist here would be the only way a third-party page
// could ever reach the API with credentials - so there is deliberately none.

builder.Services.AddOpenApi("v1", OpenApiConfiguration.Configure);

var app = builder.Build();

// The API is the single migration runner; the advisory lock serializes concurrent starts.
await MigrationRunner.MigrateAsync(
    app.Services,
    [typeof(IdentityDbContext), typeof(TenancyDbContext), typeof(BillingDbContext), typeof(NotificationsDbContext), typeof(WorkItemsDbContext), typeof(WikiDbContext), typeof(IntegrationsDbContext), typeof(AnalyticsDbContext), typeof(AutomationDbContext)]);

// Order matters: the first-run organization needs an owner, and the composition root is
// the only place that may know both modules.
var seededOwnerId = await IdentitySeeder.SeedAsync(app.Services);
await TenancySeeder.SeedAsync(app.Services, seededOwnerId);
if (runDemoSeeder || (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Seed:Demo")))
{
    await DemoSeeder.SeedAsync(app.Services);
}
if (runPerfSeeder)
{
    await PerfSeeder.SeedAsync(app.Services, seededOwnerId);
}
if (runDemoSeeder || runPerfSeeder)
{
    return;
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSecurityHeaders(
    app.Environment.IsDevelopment(),
    app.Services.GetRequiredService<IOptions<TurnstileOptions>>().Value.IsEnabled);

// In production the SPA is published into wwwroot by Aictiq.Api.csproj and served from
// this origin, so its auth cookies are first-party and there is no CORS. In development
// the Vite dev server serves it and proxies here, so wwwroot is empty and this is off.
var serveSpa = File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html"));
if (serveSpa)
{
    app.UseStaticFiles();
}

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

// Streamable HTTP reads its JSON-RPC body after routing. Reject a declared oversized
// body before parsing it and set Kestrel's streaming limit for chunked requests.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/mcp"), branch => branch.Use(async (context, next) =>
{
    if (context.Request.ContentLength is long length && length > mcpMaxRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new
        {
            title = "MCP request body is too large.",
            detail = $"MCP requests may be at most {mcpMaxRequestBodyBytes} bytes.",
            status = StatusCodes.Status413PayloadTooLarge
        }, context.RequestAborted);
        return;
    }

    var bodySize = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (bodySize is { IsReadOnly: false })
    {
        bodySize.MaxRequestBodySize = mcpMaxRequestBodyBytes;
    }

    await next();
}));

app.UseAuthentication();
app.UseCookieCsrfProtection();
// After authentication (membership needs a principal) and before authorization, so every
// endpoint filter can rely on the tenant already being established.
app.UseTenantResolution();
app.UseAuthorization();

app.MapHub<ProjectHub>(ProjectHub.Path);
app.MapMcp("/mcp")
    .RequireAuthorization(PatDefaults.Scheme)
    .RequireRateLimiting("mcp");

// The public machine-readable contract is always available by default. Operators that
// do not want to expose the interactive reference in production can set
// Documentation:EnableUi=false without breaking generators that consume the document.
if (builder.Configuration.GetValue("Documentation:Enabled", true))
{
    app.MapOpenApi();
    if (!app.Environment.IsProduction() || builder.Configuration.GetValue("Documentation:EnableUi", true))
    {
        app.MapScalarApiReference("/docs", options => options.WithTitle("Aictiq REST API"));
    }
}

app.MapDefaultEndpoints();

// Versioned API surface: module endpoint groups mount under /api/v1.
var apiV1 = app.MapGroup("/api/v1");
apiV1.MapGet("/meta", (IConfiguration configuration) => Results.Ok(ReleaseMetadata.From(configuration)))
    .AllowAnonymous()
    .WithTags("Meta")
    .WithName("getMeta")
    .WithSummary("Returns the running Aictiq version and opt-in update-check settings.")
    .Produces<ReleaseMetadata>();
apiV1.MapIdentityEndpoints();
apiV1.MapTenancyEndpoints();
apiV1.MapBillingEndpoints(app);
apiV1.MapWorkItemsEndpoints();
apiV1.MapWikiEndpoints();
apiV1.MapIntegrationsEndpoints(app, builder.Configuration);
apiV1.MapNotificationsEndpoints();
apiV1.MapAnalyticsEndpoints();
apiV1.MapAutomationEndpoints();

if (serveSpa)
{
    // The SPA owns deep links, so anything unmatched renders index.html and Vue Router
    // decides. The API's own prefixes must keep answering problem+json 404s instead of
    // an HTML page: their fallback pattern is more specific, so it wins the match.
    foreach (var prefix in SecurityHeadersMiddleware.BackendPrefixes)
    {
        app.MapFallback($"{prefix}/{{**rest}}",
            () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
    }

    app.MapFallbackToFile("index.html");
}

app.Run();

// Marker for WebApplicationFactory-based tests
public partial class Program { }
