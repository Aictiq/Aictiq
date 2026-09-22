using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

// One Garage node for the whole test run. It is an assembly fixture rather than a
// collection fixture because the API itself now needs object storage to start - its
// options are validated on start and /health/ready HEADs the bucket - so every test class
// that boots the API needs it, not only the storage tests.
[assembly: AssemblyFixture(typeof(Aictiq.IntegrationTests.Storage.GarageFixture))]

namespace Aictiq.IntegrationTests.Storage;

/// <summary>
/// A real Garage node for the storage tests.
///
/// Garage rather than MinIO on purpose: the value of these tests is proving that our
/// presigning, path-style addressing and region settings work against the server we
/// actually ship, and presigned-URL behaviour is exactly where S3 implementations differ.
/// The cost is the layout dance below - a fresh node rejects every S3 call until a layout
/// is applied - which is the same sequence <c>deploy/garage/init.sh</c> runs in Aspire and
/// compose, so this doubles as a check that the sequence is right.
/// </summary>
public sealed class GarageFixture : IAsyncLifetime
{
    private const string AdminToken = "test-admin-token";
    private const string RpcSecret =
        "3333333333333333333333333333333333333333333333333333333333333333";

    public const string Bucket = "aictiq";
    public const string AccessKey = "GKdededededededededededede";
    public const string SecretKey =
        "dededededededededededededededededededededededededededededededede";

    private readonly IContainer _container = new ContainerBuilder("dxflrs/garage:v2.1.0")
        .WithResourceMapping(
            GarageConfig(), "/etc/garage.toml")
        .WithEnvironment("GARAGE_RPC_SECRET", RpcSecret)
        .WithEnvironment("GARAGE_ADMIN_TOKEN", AdminToken)
        .WithPortBinding(3900, assignRandomHostPort: true)
        .WithPortBinding(3903, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Admin API server listening"))
        .Build();

    public string S3Endpoint { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        S3Endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(3900)}";
        await ProvisionAsync($"http://{_container.Hostname}:{_container.GetMappedPublicPort(3903)}");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Layout, key, bucket, grant - the same order as <c>deploy/garage/init.sh</c>. No
    /// idempotency guards here: the container is always fresh.
    /// </summary>
    private static async Task ProvisionAsync(string adminUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(adminUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        var status = await GetJsonAsync(http, "/v2/GetClusterStatus");
        var nodeId = status.RootElement.GetProperty("nodes")[0].GetProperty("id").GetString();

        await PostAsync(http, "/v2/UpdateClusterLayout", new
        {
            roles = new[]
            {
                new { id = nodeId, zone = "dc1", capacity = 1_000_000_000L, tags = Array.Empty<string>() }
            }
        });
        await PostAsync(http, "/v2/ApplyClusterLayout", new { version = 1 });

        await PostAsync(http, "/v2/ImportKey", new
        {
            accessKeyId = AccessKey,
            secretAccessKey = SecretKey,
            name = Bucket
        });

        var bucket = await PostAsync(http, "/v2/CreateBucket", new { globalAlias = Bucket });
        var bucketId = bucket!.RootElement.GetProperty("id").GetString();

        await PostAsync(http, "/v2/AllowBucketKey", new
        {
            bucketId,
            accessKeyId = AccessKey,
            permissions = new { read = true, write = true, owner = false }
        });
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string path)
    {
        var response = await http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument?> PostAsync(HttpClient http, string path, object body)
    {
        var response = await http.PostAsJsonAsync(path, body);
        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Garage {path} failed ({(int)response.StatusCode}): {payload}");
        }

        return string.IsNullOrWhiteSpace(payload) ? null : JsonDocument.Parse(payload);
    }

    /// <summary>
    /// Kept in step with <c>deploy/garage/garage.toml</c> by hand rather than read from
    /// disk: the test must not depend on a path relative to the test binary.
    /// </summary>
    private static byte[] GarageConfig() =>
        System.Text.Encoding.UTF8.GetBytes(
            """
            metadata_dir = "/var/lib/garage/meta"
            data_dir = "/var/lib/garage/data"
            db_engine = "sqlite"

            replication_factor = 1

            rpc_bind_addr = "[::]:3901"
            rpc_public_addr = "127.0.0.1:3901"
            rpc_secret = "0000000000000000000000000000000000000000000000000000000000000000"

            [s3_api]
            s3_region = "garage"
            api_bind_addr = "[::]:3900"
            root_domain = ".s3.garage.localhost"

            [admin]
            api_bind_addr = "[::]:3903"
            """);
}
