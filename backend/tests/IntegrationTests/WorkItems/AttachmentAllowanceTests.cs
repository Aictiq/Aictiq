using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel;
using Npgsql;

namespace Aictiq.IntegrationTests.WorkItems;

/// <summary>
/// The plan's attachment allowance, enforced where it is a guarantee rather than a hint:
/// at commitment, under a per-organization advisory lock, so two uploads that each fit
/// cannot both land. The allowance is narrowed to a few kilobytes here -
/// <c>Billing:StorageAllowanceBytes</c> only ever narrows the plan's 10 GiB - so the race
/// is the test rather than a fixture that writes ten gigabytes.
/// </summary>
[Trait("Category", "WorkItems")]
[Collection("postgres")]
public sealed class AttachmentAllowanceTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "quota-co";

    /// <summary>Two of <see cref="FileBytes"/> do not fit; one does.</summary>
    private const long Allowance = 6_000;
    private const int FileBytes = 4_096;

    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private ProjectView _project = null!;
    private WorkItemView _item = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "attach_quota", settings =>
        {
            // Quotas only exist on a hosted instance; without this the check is skipped.
            settings["Billing:Mode"] = "saas";
            settings["Billing:StorageAllowanceBytes"] = Allowance.ToString();
            settings["RateLimiting:UploadPermitLimitPerMinute"] = "1000";
        });
        var auth = await _context.RegisterAsync($"quota-{Guid.NewGuid():N}@test.local", "Quinn", "Ota");
        _client = _context.ClientFor(auth);

        var organization = await _client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("Quota", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();

        var project = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest("Files", "FIL", null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;

        var item = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects/{_project.Key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Holds the files", null, null, null, null, null, null, null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        item.EnsureSuccessStatusCode();
        _item = (await item.Content.ReadFromJsonAsync<WorkItemView>(ApiTestContext.Json, Ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task concurrent_commits_cannot_both_fit_and_the_refused_one_stays_pending_until_space_is_freed()
    {
        // Both uploads succeed: storage_bytes counts committed attachments only, so neither
        // upload sees the other. That is exactly why the commit is where the guarantee is.
        var first = await UploadAsync("first.txt");
        var second = await UploadAsync("second.txt");
        Assert.Equal(0, await CommittedBytesAsync());

        var responses = await Task.WhenAll(CommitAsync(first.Id), CommitAsync(second.Id));

        var accepted = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK);
        var committed = (await accepted.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, Ct))!;
        var rejected = committed.Id == first.Id ? second : first;

        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemTypes.PlanLimit, problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("storage_bytes", problem.RootElement.GetProperty("limit").GetString());
        // There is no higher tier to sell, so no upgrade is offered.
        Assert.True(!problem.RootElement.TryGetProperty("upgradeUrl", out var upgrade)
            || upgrade.ValueKind == JsonValueKind.Null, "A storage refusal must not offer an upgrade URL.");
        Assert.Contains("Delete attachments", problem.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // Exactly one landed, and the organization is inside its allowance rather than over it.
        Assert.Equal(FileBytes, await CommittedBytesAsync());
        Assert.True(await CommittedBytesAsync() <= Allowance);
        Assert.Equal(1, await StatusCountAsync(AttachmentStatus.Committed));
        Assert.Equal(1, await StatusCountAsync(AttachmentStatus.Pending));
        Assert.Equal(1, await ScalarAsync(
            $"SELECT count(*) FROM work.attachments WHERE id = '{rejected.Id}' AND status = {(short)AttachmentStatus.Pending}"));

        // Over the line and still fully readable: the refusal took nothing away.
        var download = await _client.GetAsync($"/api/v1/orgs/{Slug}/attachments/{committed.Id}/download", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(FileBytes, (await download.Content.ReadAsByteArrayAsync(Ct)).Length);
        var listed = await _client.GetFromJsonAsync<List<AttachmentView>>(
            $"/api/v1/orgs/{Slug}/items/{_item.Key}/attachments", ApiTestContext.Json, Ct);
        Assert.Equal(committed.Id, Assert.Single(listed!).Id);

        // Deleting is the remedy the message names, and it works.
        var deleted = await _client.DeleteAsync($"/api/v1/orgs/{Slug}/attachments/{committed.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await CommittedBytesAsync());

        var retried = await CommitAsync(rejected.Id);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(FileBytes, await CommittedBytesAsync());
    }

    [Fact]
    public async Task an_upload_that_alone_exceeds_the_allowance_is_refused_before_anything_is_stored()
    {
        var over = await UploadRawAsync("huge.txt", new byte[Allowance + 1]);

        Assert.Equal(HttpStatusCode.PaymentRequired, over.StatusCode);
        using var problem = JsonDocument.Parse(await over.Content.ReadAsStringAsync(Ct));
        Assert.Equal("storage_bytes", problem.RootElement.GetProperty("limit").GetString());
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM work.attachments"));
    }

    // --------------------------------------------------------------------------- helpers

    private async Task<AttachmentView> UploadAsync(string name)
    {
        var response = await UploadRawAsync(name, Payload());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, Ct))!;
    }

    private async Task<HttpResponseMessage> UploadRawAsync(string name, byte[] payload)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        return await _client.PostAsync($"/api/v1/orgs/{Slug}/projects/{_project.Key}/attachments", form, Ct);
    }

    private Task<HttpResponseMessage> CommitAsync(Guid attachmentId) =>
        _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/attachments/{attachmentId}/commit",
            new CommitAttachmentRequest(_item.Id, null), ApiTestContext.Json, Ct);

    /// <summary>Text, not an image: images are re-encoded to WebP and would not be this size.</summary>
    private static byte[] Payload() => Enumerable.Repeat((byte)'a', FileBytes).ToArray();

    private Task<long> CommittedBytesAsync() => ScalarAsync(
        $"SELECT coalesce(sum(size_bytes), 0) FROM work.attachments WHERE status = {(short)AttachmentStatus.Committed}");

    private Task<long> StatusCountAsync(AttachmentStatus status) => ScalarAsync(
        $"SELECT count(*) FROM work.attachments WHERE status = {(short)status}");

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }
}
