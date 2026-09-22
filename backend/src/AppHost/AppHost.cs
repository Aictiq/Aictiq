var builder = DistributedApplication.CreateBuilder(args);

// RLS is useful in local development only when the services use the constrained role
// too. Aspire's generated PostgreSQL login remains the database owner/migration login;
// the one-shot resource below creates and maintains the separate runtime login.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("aictiq-pgdata");

if (builder.ExecutionContext.IsRunMode)
{
    postgres = postgres.WithPgAdmin();
}

var appDb = postgres.AddDatabase("appdb");

var postgresAppPassword = builder.AddParameter("postgres-app-password", secret: true);
var postgresRoles = builder.AddContainer("postgres-roles", "postgres", "17")
    .WithReference(appDb)
    .WithBindMount("../../../deploy/postgres/apphost-roles.sql", "/roles.sql", isReadOnly: true)
    .WithEntrypoint("sh")
    .WithArgs("-ec", "psql \"$APPDB_URI\" --set=ON_ERROR_STOP=1 --set=app_password=\"$POSTGRES_APP_PASSWORD\" --file=/roles.sql")
    .WithEnvironment("POSTGRES_APP_PASSWORD", postgresAppPassword)
    .WaitFor(appDb);

// Keep migrations on Aspire's generated owner connection, but make every normal API
// connection exercise the same RLS-constrained role as compose. ReferenceExpression
// keeps the endpoint allocation and secret parameter deferred until Aspire starts.
var appDbRuntimeConnection = ReferenceExpression.Create(
    $"Host={appDb.Resource.GetConnectionProperty("Host")};" +
    $"Port={appDb.Resource.GetConnectionProperty("Port")};" +
    $"Database={appDb.Resource.GetConnectionProperty("DatabaseName")};" +
    $"Username=aictiq_app;Password={postgresAppPassword}");

// Secrets flow as Aspire parameters (persisted in the AppHost's user secrets in run
// mode) - no .env files anywhere. Publish mode prompts/binds them per environment.
var jwtKey = builder.AddParameter("jwt-key", secret: true);
var garageRpcSecret = builder.AddParameter("garage-rpc-secret", secret: true);
var garageAdminToken = builder.AddParameter("garage-admin-token", secret: true);
var garageAccessKey = builder.AddParameter("garage-access-key", secret: true);
var garageSecretKey = builder.AddParameter("garage-secret-key", secret: true);

// ---------------------------------------------------------------- object storage
// Garage speaks S3 and nothing else here knows it is Garage; MinIO, AWS or R2 drop in by
// changing the S3__* variables (docs/self-host.md).
var garage = builder.AddContainer("garage", "dxflrs/garage", "v2.1.0")
    .WithBindMount("../../../deploy/garage/garage.toml", "/etc/garage.toml", isReadOnly: true)
    .WithVolume("aictiq-garage", "/var/lib/garage")
    .WithEnvironment("GARAGE_RPC_SECRET", garageRpcSecret)
    .WithEnvironment("GARAGE_ADMIN_TOKEN", garageAdminToken)
    .WithEndpoint(targetPort: 3900, name: "s3", scheme: "http")
    .WithEndpoint(targetPort: 3903, name: "admin", scheme: "http");

var garageS3 = garage.GetEndpoint("s3");
var garageAdmin = garage.GetEndpoint("admin");

// A fresh Garage node rejects every S3 call until a layout is applied, so the bucket and
// key have to be created before the API starts. The script is idempotent; the Garage
// image has no shell, hence a separate curl container against the admin API.
var garageInit = builder.AddContainer("garage-init", "alpine/curl", "8.19.0")
    .WithBindMount("../../../deploy/garage/init.sh", "/init.sh", isReadOnly: true)
    .WithEntrypoint("sh")
    .WithArgs("/init.sh")
    .WithEnvironment("GARAGE_ADMIN_URL", garageAdmin)
    .WithEnvironment("GARAGE_ADMIN_TOKEN", garageAdminToken)
    .WithEnvironment("GARAGE_BUCKET", "aictiq")
    .WithEnvironment("GARAGE_ACCESS_KEY", garageAccessKey)
    .WithEnvironment("GARAGE_SECRET_KEY", garageSecretKey)
    .WithEnvironment("GARAGE_CAPACITY_BYTES", "10737418240")
    .WithEnvironment("GARAGE_ZONE", "dc1")
    .WaitFor(garage);

// ---------------------------------------------------------------- email
// Mailpit is a local SMTP sink with a web inbox: development sends real messages that
// really arrive, at nobody. Run mode only - a published deployment either has a relay in
// its configuration or has none at all, and "none at all" is a supported way to run
// Aictiq (see docs/self-host.md).
IResourceBuilder<ContainerResource>? mailpit = null;
if (builder.ExecutionContext.IsRunMode)
{
    mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.28")
        .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
        .WithHttpEndpoint(targetPort: 8025, name: "ui")
        .WithLifetime(ContainerLifetime.Persistent);
}

// ---------------------------------------------------------------- billing (optional)
// Stripe is SaaS-only and, like SMTP, absent by default: a local stack without keys runs
// self-hosted and the billing endpoints say so. To try checkout and webhooks locally, set
// the two secret parameters in the AppHost's user secrets (Parameters:stripe-secret-key,
// Parameters:stripe-webhook-secret), plus Billing:Mode=saas and Stripe:Prices:<plan>_<kind>
// as plain configuration - see docs/billing.md. A parameter is only declared when it has a
// value, so the dashboard never blocks start-up asking for one nobody needs.
var stripe = new StripeSettings(
    OptionalSecret(builder, "stripe-secret-key"),
    OptionalSecret(builder, "stripe-webhook-secret"),
    builder.Configuration["Billing:Mode"],
    builder.Configuration.GetSection("Stripe:Prices").GetChildren()
        .Where(price => !string.IsNullOrWhiteSpace(price.Value))
        .ToDictionary(price => price.Key, price => price.Value!));

// ---------------------------------------------------------------- services
var api = builder.AddProject<Projects.Aictiq_Api>("api")
    .WithReference(appDb)
    .WithReference(appDb, "appdb-admin")
    .WithEnvironment("ConnectionStrings__appdb", appDbRuntimeConnection)
    .WaitFor(appDb)
    .WaitForCompletion(postgresRoles)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithGarage(garageS3, garageAccessKey, garageSecretKey)
    .WithMailpit(mailpit)
    .WithStripe(stripe)
    .WaitForCompletion(garageInit)
    .WithExternalHttpEndpoints();

// The SPA is same-origin with the API: Vite proxies /api, /mcp, /hubs and /s3 to
// AICTIQ_API_BASE in dev, and the API serves the built dist/ in production.
builder.AddViteApp("web", "../../../frontend-vue")
    // The web app is on pnpm (see frontend-vue/pnpm-workspace.yaml); the version is
    // pinned by package.json's "packageManager" field.
    .WithPnpm()
    // Keep a stable public Aspire URL while Vite listens on its development-server port.
    .WithHttpEndpoint(targetPort: 5173, port: 21212)
    .WithEnvironment("AICTIQ_API_BASE", api.GetEndpoint("http"))
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Aictiq_Workers>("workers")
    .WithReference(appDb)
    .WithEnvironment("ConnectionStrings__appdb", appDbRuntimeConnection)
    .WithEnvironment("Jwt__Key", jwtKey)
    .WithGarage(garageS3, garageAccessKey, garageSecretKey)
    // Workers are the only process that actually sends; the API only needs the config to
    // report on /health/ready whether sending is possible at all.
    .WithMailpit(mailpit)
    // Workers sync seat quantities to Stripe; the API creates sessions and takes webhooks.
    .WithStripe(stripe)
    // The API owns migrations (advisory-lock guarded); workers wait for it to be up.
    .WaitFor(api);

builder.Build().Run();

static IResourceBuilder<ParameterResource>? OptionalSecret(IDistributedApplicationBuilder builder, string name) =>
    string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{name}"])
        ? null
        : builder.AddParameter(name, secret: true);

file sealed record StripeSettings(
    IResourceBuilder<ParameterResource>? SecretKey,
    IResourceBuilder<ParameterResource>? WebhookSecret,
    string? BillingMode,
    IReadOnlyDictionary<string, string> Prices);

file static class StripeExtensions
{
    /// <summary>
    /// Passes whatever Stripe configuration exists and nothing else: an unset key stays unset
    /// rather than becoming an empty string the service would have to tell apart from one.
    /// </summary>
    public static IResourceBuilder<T> WithStripe<T>(this IResourceBuilder<T> service, StripeSettings stripe)
        where T : IResourceWithEnvironment
    {
        if (stripe.SecretKey is not null) service = service.WithEnvironment("Stripe__SecretKey", stripe.SecretKey);
        if (stripe.WebhookSecret is not null) service = service.WithEnvironment("Stripe__WebhookSecret", stripe.WebhookSecret);
        if (!string.IsNullOrWhiteSpace(stripe.BillingMode)) service = service.WithEnvironment("Billing__Mode", stripe.BillingMode);
        foreach (var (key, price) in stripe.Prices)
        {
            service = service.WithEnvironment($"Stripe__Prices__{key}", price);
        }
        return service;
    }
}

file static class GarageExtensions
{
    /// <summary>
    /// Points a service at the dev Garage node. In development the browser reaches Garage
    /// on the same host-mapped address the services use, so the public endpoint is left to
    /// default; behind a reverse proxy (compose, production) set <c>S3__PublicEndpoint</c>
    /// to whatever the browser resolves, because presigned URLs are signed for that host.
    /// </summary>
    public static IResourceBuilder<T> WithGarage<T>(
        this IResourceBuilder<T> service,
        EndpointReference s3,
        IResourceBuilder<ParameterResource> accessKey,
        IResourceBuilder<ParameterResource> secretKey)
        where T : IResourceWithEnvironment =>
        service
            .WithEnvironment("S3__Endpoint", s3)
            .WithEnvironment("S3__Bucket", "aictiq")
            .WithEnvironment("S3__AccessKey", accessKey)
            .WithEnvironment("S3__SecretKey", secretKey)
            .WithEnvironment("S3__Region", "garage")
            .WithEnvironment("S3__ForcePathStyle", "true");

    /// <summary>
    /// Points a service at the dev mail sink. Plaintext on 1025 with no credentials -
    /// Mailpit accepts anything, which is the whole point of a sink - so STARTTLS is
    /// explicitly off rather than left to the production default.
    /// </summary>
    public static IResourceBuilder<T> WithMailpit<T>(
        this IResourceBuilder<T> service, IResourceBuilder<ContainerResource>? mailpit)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        if (mailpit is null)
        {
            return service;
        }

        var smtp = mailpit.GetEndpoint("smtp");
        return service
            .WithEnvironment("Email__Smtp__Host", smtp.Property(EndpointProperty.Host))
            .WithEnvironment("Email__Smtp__Port", smtp.Property(EndpointProperty.Port))
            .WithEnvironment("Email__Smtp__UseStartTls", "false")
            .WithEnvironment("Email__FromAddress", "aictiq@localhost")
            .WithEnvironment("Email__FromName", "Aictiq (dev)")
            .WaitFor(mailpit);
    }
}
