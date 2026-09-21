using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

namespace Microsoft.Extensions.Hosting;

// Common Aspire service defaults: service discovery, resilience, health checks, OpenTelemetry, Serilog.
// Referenced by the Api and Workers projects.
public static class Extensions
{
    private const string LivenessEndpointPath = "/health/live";
    private const string ReadinessEndpointPath = "/health/ready";

    /// <summary>
    /// Tag on a health check registration that says its description is safe to publish in
    /// the readiness body. Everything else contributes only to the overall status.
    ///
    /// /health is reachable from the internet in the compose deployment (deploy/Caddyfile),
    /// and check descriptions carry internal detail — the object store's endpoint, a
    /// connection failure's message. So publishing is opt-in per registration, and the
    /// descriptions that opt in are written to be facts about the deployment rather than
    /// about its insides (see EmailHealthCheck).
    ///
    /// SharedKernel does not reference this project, so its registrations spell the tag
    /// out; the two spellings are one literal apart, like "ready" already is.
    /// </summary>
    public const string PublicHealthTag = "public";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureSerilog();
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureSerilog<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", builder.Environment.ApplicationName)
            .WriteTo.Console());

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Outbox delivery counters (dead_lettered is the one to alert on).
                    // Literal, not OutboxProcessor.MeterName: ServiceDefaults stays free
                    // of a SharedKernel reference. Keep the two in sync.
                    .AddMeter("Aictiq.Outbox")
                    .AddMeter("Aictiq.Mcp")
                    .AddMeter("Aictiq.Email")
                    // Authentication lockouts are intentionally low-cardinality security
                    // signals. Alert on a sustained rise, not on individual accounts.
                    .AddMeter("Aictiq.Identity")
                    // Factory runs: started, finished per outcome, and
                    // runner_lost — the dead-letter of the factory, something to alert on.
                    .AddMeter("Aictiq.Automation");
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health")
                    )
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    // /health/live and /health/ready are required in every environment (orchestrator
    // probes and alerting depend on them), and both are reachable from the internet in
    // the compose deployment. Liveness returns status only; readiness adds the
    // descriptions of checks tagged PublicHealthTag and nothing else, so exposing them in
    // production stays acceptable.
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(LivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        app.MapHealthChecks(ReadinessEndpointPath, new HealthCheckOptions
        {
            ResponseWriter = WritePublicReadinessAsync
        });

        return app;
    }

    /// <summary>
    /// The overall status, plus the description of every check that opted in via
    /// <see cref="PublicHealthTag"/>. Never an exception, never a check that did not ask
    /// to be listed — this body is public.
    /// </summary>
    private static Task WritePublicReadinessAsync(HttpContext context, HealthReport report)
    {
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries
                .Where(entry => entry.Value.Tags.Contains(PublicHealthTag))
                .ToDictionary(entry => entry.Key, entry => entry.Value.Description)
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(payload);
    }
}
