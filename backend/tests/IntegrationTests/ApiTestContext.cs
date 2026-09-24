using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Identity;
using Aictiq.Modules.Integrations;
using Aictiq.Modules.Identity.Auth;
using Aictiq.Modules.Identity.Endpoints;
using Aictiq.Modules.Notifications;
using Aictiq.Modules.Tenancy;
using Aictiq.Modules.Wiki;
using Aictiq.Modules.WorkItems;

namespace Aictiq.IntegrationTests;

/// <summary>
/// Boots the real API against a dedicated test database and drives it exclusively over
/// HTTP - user setup goes through the same endpoints production uses.
/// </summary>
public sealed class ApiTestContext : IAsyncDisposable
{
    /// <summary>
    /// What a real client sends and receives: camelCase properties and camelCase enum
    /// *names*, matching the API's ConfigureHttpJsonOptions. Tests that read an enum
    /// must use these, or they assert against a serialization the API does not produce.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin.Test.Password1";
    public const string DefaultPassword = "User.Test.Password1";

    public WebApplicationFactory<Program> Factory { get; }
    public string ConnectionString { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;

    /// <summary>Present only when the context was created with countQueries: true.</summary>
    public QueryCounter? QueryCount { get; private set; }

    private string? _appLogin;

    private ApiTestContext(WebApplicationFactory<Program> factory) => Factory = factory;

    /// <param name="garage">
    /// The API validates its S3 options on start and reports the bucket on
    /// /health/ready, so booting it needs a real store - not a placeholder endpoint.
    /// </param>
    /// <param name="appRole">
    /// Serve requests as a login in <c>aictiq_app</c> - the role production runs as, which
    /// row-level security binds - and migrate over <c>appdb-admin</c>, as Aspire does. The
    /// default superuser bypasses RLS, so a query that forgets to tell Postgres the tenant
    /// passes every test and returns nothing in production; raw-SQL paths want this on.
    /// </param>
    /// <param name="countQueries">
    /// Wires a QueryCounter interceptor into every module context and drops the host's
    /// background sweeps, whose own polls would otherwise bleed into a request's
    /// measurement.
    /// </param>
    /// <param name="signInAdmin">
    /// Off for a host whose sign-in the test wants to exercise from the first request -
    /// one that challenges logins, say - and which therefore leaves <see cref="Admin"/> unset.
    /// </param>
    public static async Task<ApiTestContext> CreateAsync(
        PostgresFixture postgres, GarageFixture garage, string dbPrefix,
        Action<IDictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        bool countQueries = false,
        bool appRole = false,
        bool signInAdmin = true)
    {
        var connectionString = await postgres.CreateDatabaseAsync(dbPrefix);
        string? appLogin = null;
        var requestConnectionString = connectionString;
        if (appRole)
        {
            appLogin = $"aictiq_test_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            // Identity's migration creates aictiq_app too, but the login must exist before
            // the API boots, and a role is cluster-wide, so another test may already have
            // it - or be creating it right now, which is why this catches rather than
            // checks first. The login itself is unique per context.
            await using var create = new NpgsqlCommand($"""
                DO $$
                BEGIN
                    CREATE ROLE aictiq_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
                EXCEPTION WHEN duplicate_object OR unique_violation THEN
                    NULL;
                END $$;
                CREATE ROLE "{appLogin}" LOGIN PASSWORD 'app-role-test-password' IN ROLE aictiq_app;
                """, admin);
            await create.ExecuteNonQueryAsync();
            requestConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Username = appLogin,
                Password = "app-role-test-password",
            }.ConnectionString;
        }
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:appdb"] = requestConnectionString,
            ["Jwt:Key"] = "integration-test-signing-key-0123456789abcdef",
            ["Seed:AdminEmail"] = AdminEmail,
            ["Seed:AdminPassword"] = AdminPassword,
            // WebApplicationFactory boots in Development, so appsettings.Development.json
            // is in play. Blank it here rather than inherit a first-run organization the
            // test did not ask for; the seeding tests set it back deliberately.
            ["Seed:OrganizationName"] = "",
            ["Seed:OrganizationSlug"] = "",
            ["Seed:OrganizationTimeZone"] = "",
            ["S3:Endpoint"] = garage.S3Endpoint,
            ["S3:Bucket"] = GarageFixture.Bucket,
            ["S3:AccessKey"] = GarageFixture.AccessKey,
            ["S3:SecretKey"] = GarageFixture.SecretKey,
            ["S3:Region"] = "garage",
            ["S3:ForcePathStyle"] = "true",
            // Tests hammer the auth endpoints far past the production budget.
            ["RateLimiting:AuthPermitLimitPerMinute"] = "1000",
            ["RateLimiting:GlobalPermitLimitPerMinute"] = "10000",
            ["RateLimiting:McpPermitLimitPerMinute"] = "10000",
            ["RateLimiting:McpRequestPermitLimitPerMinute"] = "10000"
        };
        if (appRole) settings["ConnectionStrings:appdb-admin"] = connectionString;
        configure?.Invoke(settings);

        var counter = countQueries ? new QueryCounter() : null;
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
            if (counter is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    // Sweep workers poll on their own timers (the CSV import worker every
                    // 2 s), which would bleed into a request's count; a counting context
                    // runs none of them.
                    services.RemoveAll<IHostedService>();
                    CountQueriesIn<IdentityDbContext>(services, counter);
                    CountQueriesIn<TenancyDbContext>(services, counter);
                    CountQueriesIn<NotificationsDbContext>(services, counter);
                    CountQueriesIn<WorkItemsDbContext>(services, counter);
                    CountQueriesIn<WikiDbContext>(services, counter);
                    CountQueriesIn<IntegrationsDbContext>(services, counter);
                    CountQueriesIn<AnalyticsDbContext>(services, counter);
                });
            }
        });

        var context = new ApiTestContext(factory) { ConnectionString = connectionString, QueryCount = counter, _appLogin = appLogin };
        if (signInAdmin)
        {
            context.Admin = await context.ClientForAsync(AdminEmail, AdminPassword);
        }
        return context;
    }

    /// <summary>
    /// The seam EF hands out for options additions from DI: AddDbContext resolves every
    /// IDbContextOptionsConfiguration&lt;TContext&gt; and applies it after the module's own
    /// registration, so the interceptor lands beside the auditing one.
    /// </summary>
    private static void CountQueriesIn<TContext>(IServiceCollection services, QueryCounter counter)
        where TContext : DbContext =>
        services.AddSingleton<IDbContextOptionsConfiguration<TContext>>(new QueryCountingOptions<TContext>(counter));

    public HttpClient Anonymous() => Factory.CreateClient();

    /// <summary>
    /// A client that looks like the SPA: it sends X-Aictiq-Request, so the API answers in
    /// cookie mode and enforces the CSRF check. Cookies are not persisted automatically -
    /// tests replay the ones they care about.
    /// </summary>
    public HttpClient Browser()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthCookies.RequestHeaderName, "1");
        return client;
    }

    public async Task<AuthResponse> LoginAsync(string email, string password)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Self-registration is on in Aictiq, so tests mint users the same way clients do.</summary>
    public async Task<AuthResponse> RegisterAsync(
        string email, string firstName = "Test", string lastName = "User", string password = DefaultPassword)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, firstName, lastName));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public HttpClient ClientFor(AuthResponse auth)
    {
        var client = Factory.CreateClient();
        // Bearer mode: the token is only null when the caller asked for cookie mode.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            auth.AccessToken ?? throw new InvalidOperationException("Expected a bearer-mode AuthResponse."));
        return client;
    }

    public async Task<HttpClient> ClientForAsync(string email, string password) =>
        ClientFor(await LoginAsync(email, password));

    public async ValueTask DisposeAsync()
    {
        Admin?.Dispose();
        await Factory.DisposeAsync();
        if (_appLogin is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{_appLogin}\";", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
