using System.Net;
using System.Text;
using Aictiq.SharedKernel.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aictiq.IntegrationTests.Storage;

/// <summary>
/// Drives <see cref="IBlobStorage"/> against a real Garage node. The presigned URLs are
/// exercised with a bare <see cref="HttpClient"/> — no AWS SDK, no credentials — because
/// that is what a browser is: if the signature is wrong, or the endpoint the URL names is
/// not the one the browser can reach, these fail exactly as production would.
/// </summary>
public sealed class BlobStorageTests(GarageFixture garage)
{
    private static readonly HttpClient Browser = new();

    private S3BlobStorage CreateStorage(Action<S3StorageOptions>? configure = null)
    {
        var options = new S3StorageOptions
        {
            Endpoint = garage.S3Endpoint,
            Bucket = GarageFixture.Bucket,
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
            Region = "garage",
            ForcePathStyle = true,
        };
        configure?.Invoke(options);
        return new S3BlobStorage(Options.Create(options));
    }

    private static string Key(string suffix) =>
        $"org/{Guid.CreateVersion7()}/project/{Guid.CreateVersion7()}/attachments/{suffix}";

    [Fact]
    public async Task presigned_upload_then_head_then_presigned_download_round_trips_the_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("round-trip.txt");
        var payload = "the quick brown fox"u8.ToArray();

        var upload = await storage.PresignUploadAsync(key, "text/plain", maxBytes: 1024, cancellationToken: ct);

        using var put = new HttpRequestMessage(HttpMethod.Put, upload)
        {
            Content = new ByteArrayContent(payload)
        };
        put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var putResponse = await Browser.SendAsync(put, ct);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var metadata = await storage.HeadAsync(key, ct);
        Assert.NotNull(metadata);
        Assert.Equal(payload.Length, metadata.ContentLength);
        Assert.Equal("text/plain", metadata.ContentType);

        var download = await storage.PresignDownloadAsync(key, cancellationToken: ct);
        var downloaded = await Browser.GetByteArrayAsync(download, ct);
        Assert.Equal(payload, downloaded);
    }

    [Fact]
    public async Task the_signed_content_type_is_binding_on_the_uploader()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("content-type.png");

        var upload = await storage.PresignUploadAsync(key, "image/png", maxBytes: 1024, cancellationToken: ct);

        // A client that was granted an image upload must not be able to store HTML —
        // the content type is part of the signature, so changing it breaks it.
        using var put = new HttpRequestMessage(HttpMethod.Put, upload)
        {
            Content = new ByteArrayContent("<script>alert(1)</script>"u8.ToArray())
        };
        put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");

        var response = await Browser.SendAsync(put, ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await storage.ExistsAsync(key, ct));
    }

    [Fact]
    public async Task a_download_url_names_the_file_without_exposing_the_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("opaque-key");
        await UploadAsync(storage, key, "text/plain", "hello"u8.ToArray(), ct);

        var download = await storage.PresignDownloadAsync(key, "Quarterly Report.pdf", cancellationToken: ct);
        var response = await Browser.GetAsync(download, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Quarterly Report.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task a_filename_cannot_inject_a_response_header()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("header-injection");
        await UploadAsync(storage, key, "text/plain", "hello"u8.ToArray(), ct);

        var download = await storage.PresignDownloadAsync(
            key, "evil\r\nX-Injected: yes\".pdf", cancellationToken: ct);
        var response = await Browser.GetAsync(download, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Injected"));
    }

    [Fact]
    public async Task an_expired_url_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("expired");
        await UploadAsync(storage, key, "text/plain", "hello"u8.ToArray(), ct);

        // The URL is the credential; a stale one must be worthless. A real (rather than
        // negative) TTL is used so this exercises the expiry path Garage actually takes,
        // which is why it waits rather than signing something already in the past.
        var download = await storage.PresignDownloadAsync(
            key, ttl: TimeSpan.FromSeconds(1), cancellationToken: ct);
        Assert.Equal(HttpStatusCode.OK, (await Browser.GetAsync(download, ct)).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        var response = await Browser.GetAsync(download, ct);
        // Garage rejects an expired signature with 400; AWS S3 uses 403. What matters is
        // that the URL is refused, so both are accepted rather than pinning one server's
        // choice of status code.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains(
            response.StatusCode, (HttpStatusCode[])[HttpStatusCode.BadRequest, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task head_and_exists_answer_false_for_a_missing_object_rather_than_throwing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("never-uploaded");

        Assert.Null(await storage.HeadAsync(key, ct));
        Assert.False(await storage.ExistsAsync(key, ct));
    }

    [Fact]
    public async Task delete_removes_the_object_and_deleting_again_is_not_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage();
        var key = Key("deleted");
        await UploadAsync(storage, key, "text/plain", "hello"u8.ToArray(), ct);
        Assert.True(await storage.ExistsAsync(key, ct));

        await storage.DeleteAsync(key, ct);
        Assert.False(await storage.ExistsAsync(key, ct));

        // Blob garbage collection re-runs after a partial failure; a second delete must
        // not turn a completed cleanup into a crash.
        await storage.DeleteAsync(key, ct);
    }

    /// <summary>
    /// The point of <c>PublicEndpoint</c>: URLs are signed for the address the browser
    /// uses, which behind a reverse proxy is not the address the API uses.
    /// </summary>
    [Fact]
    public async Task presigned_urls_are_built_from_the_public_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var storage = CreateStorage(o => o.PublicEndpoint = "https://files.example.com");

        var upload = await storage.PresignUploadAsync(
            Key("public"), "text/plain", maxBytes: 1024, cancellationToken: ct);

        Assert.Equal("files.example.com", upload.Host);
        Assert.Equal("https", upload.Scheme);
    }

    /// <summary>
    /// The compose deployment serves Garage under <c>/s3</c> on the app's own origin, and
    /// Caddy strips that prefix before Garage sees the request. So the prefix must be
    /// added <em>after</em> signing — signing the prefixed path would mean the two ends
    /// verify different paths and every upload would fail.
    /// </summary>
    [Fact]
    public async Task the_public_path_prefix_is_added_after_signing_not_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Key("prefixed");

        using var direct = CreateStorage();
        using var proxied = CreateStorage(o => o.PublicPathPrefix = "/s3");

        var withPrefix = await proxied.PresignDownloadAsync(key, cancellationToken: ct);
        var withoutPrefix = await direct.PresignDownloadAsync(key, cancellationToken: ct);

        Assert.StartsWith("/s3/", withPrefix.AbsolutePath);
        // Strip the prefix back off, as the proxy does, and the path is byte-identical to
        // the unprefixed one — which is what the store signs against.
        Assert.Equal(withoutPrefix.AbsolutePath, withPrefix.AbsolutePath["/s3".Length..]);

        // And the signature really is over the stripped path: the unprefixed URL works.
        Assert.Equal(
            (await Browser.GetAsync(withoutPrefix, ct)).StatusCode,
            (await Browser.GetAsync(StripPrefix(withPrefix), ct)).StatusCode);
    }

    private static Uri StripPrefix(Uri url) =>
        new UriBuilder(url) { Path = url.AbsolutePath["/s3".Length..] }.Uri;

    [Fact]
    public async Task the_health_check_is_healthy_against_a_provisioned_bucket()
    {
        var ct = TestContext.Current.CancellationToken;
        using var check = new StorageHealthCheck(Options.Create(new S3StorageOptions
        {
            Endpoint = garage.S3Endpoint,
            Bucket = GarageFixture.Bucket,
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// A probe that hangs is worse than one that fails — the orchestrator waits on its own
    /// timeout while the request sits open. An unroutable address is the cheapest way to
    /// make the store "there but never answering".
    /// </summary>
    [Fact]
    public async Task the_health_check_gives_up_rather_than_hanging_on_an_unreachable_store()
    {
        var ct = TestContext.Current.CancellationToken;
        using var check = new StorageHealthCheck(Options.Create(new S3StorageOptions
        {
            // TEST-NET-1 (RFC 5737): routable nowhere, so the connection never completes.
            Endpoint = "http://192.0.2.1:3900",
            Bucket = GarageFixture.Bucket,
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
        }));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);
        started.Stop();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15),
            $"the probe took {started.Elapsed.TotalSeconds:0.0}s; it must fail fast");
    }

    [Fact]
    public async Task the_health_check_is_unhealthy_when_the_bucket_is_not_there()
    {
        var ct = TestContext.Current.CancellationToken;
        using var check = new StorageHealthCheck(Options.Create(new S3StorageOptions
        {
            Endpoint = garage.S3Endpoint,
            Bucket = "no-such-bucket",
            AccessKey = GarageFixture.AccessKey,
            SecretKey = GarageFixture.SecretKey,
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), ct);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static async Task UploadAsync(
        IBlobStorage storage, string key, string contentType, byte[] payload, CancellationToken ct)
    {
        var url = await storage.PresignUploadAsync(key, contentType, maxBytes: 4096, cancellationToken: ct);
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(payload) };
        put.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        (await Browser.SendAsync(put, ct)).EnsureSuccessStatusCode();
    }
}
