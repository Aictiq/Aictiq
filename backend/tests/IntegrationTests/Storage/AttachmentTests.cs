using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SkiaSharp;
using Aictiq.IntegrationTests.WorkItems;
using Aictiq.Modules.WorkItems;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.Modules.WorkItems.Workers;
using Aictiq.SharedKernel.Storage;

namespace Aictiq.IntegrationTests.Storage;

/// <summary>HTTP coverage for the metadata guard around direct-to-Garage attachment uploads.</summary>
[Trait("Category", "Storage")]
public sealed class AttachmentTests(PostgresFixture postgres, GarageFixture garage) : WorkItemsTestBase(postgres, garage)
{
    [Fact]
    public async Task upload_rejects_unapproved_type_empty_file_and_a_fake_image_before_creating_metadata()
    {
        var type = await UploadAsync("report.html", "text/html", "<p>hi</p>"u8.ToArray());
        var empty = await UploadAsync("empty.txt", "text/plain", []);
        var fake = await UploadAsync("shot.png", "image/png", "not a png"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, type.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
        Assert.Equal(0L, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task an_image_is_stored_as_webp_scaled_to_the_maximum_width()
    {
        var attachment = await UploadOkAsync("screenshot.png", "image/png", Png(3000, 1500));

        Assert.Equal("image/webp", attachment.ContentType);
        Assert.Equal("screenshot.webp", attachment.FileName);
        var item = await CreateAsync(WorkItemType.Bug, "Attachment owner");
        (await Client.PostAsJsonAsync(AttachmentPath(attachment.Id, "commit"), new CommitAttachmentRequest(item.Id, null), ApiTestContext.Json, CancellationToken))
            .EnsureSuccessStatusCode();

        var download = await Client.GetAsync(AttachmentPath(attachment.Id, "download"), CancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/webp", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("private", download.Headers.CacheControl?.ToString());
        var bytes = await download.Content.ReadAsByteArrayAsync(CancellationToken);
        Assert.Equal(attachment.SizeBytes, bytes.LongLength);
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(1920, codec.Info.Width);
        Assert.Equal(960, codec.Info.Height);
    }

    [Fact]
    public async Task a_non_image_is_stored_as_uploaded_and_downloads_as_an_attachment()
    {
        var attachment = await UploadOkAsync("notes.txt", "text/plain", "hello"u8.ToArray());
        var item = await CreateAsync(WorkItemType.Bug, "Attachment owner");
        (await Client.PostAsJsonAsync(AttachmentPath(attachment.Id, "commit"), new CommitAttachmentRequest(item.Id, null), ApiTestContext.Json, CancellationToken))
            .EnsureSuccessStatusCode();

        var download = await Client.GetAsync(AttachmentPath(attachment.Id, "download"), CancellationToken);

        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("hello", await download.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task item_attachment_list_includes_comment_attachments_only_when_requested()
    {
        var item = await CreateAsync(WorkItemType.Bug, "Attachment context");
        var itemAttachment = await UploadOkAsync("description.txt", "text/plain", "description"u8.ToArray());
        (await Client.PostAsJsonAsync(AttachmentPath(itemAttachment.Id, "commit"), new CommitAttachmentRequest(item.Id, null), ApiTestContext.Json, CancellationToken)).EnsureSuccessStatusCode();

        var commentResponse = await Client.PostAsJsonAsync($"/api/v1/orgs/work-items/items/{item.Key}/comments/",
            new CreateCommentRequest("The screenshot is attached."), ApiTestContext.Json, CancellationToken);
        commentResponse.EnsureSuccessStatusCode();
        var comment = (await commentResponse.Content.ReadFromJsonAsync<CommentView>(ApiTestContext.Json, CancellationToken))!;
        var commentAttachment = await UploadOkAsync("comment.txt", "text/plain", "comment"u8.ToArray());
        (await Client.PostAsJsonAsync(AttachmentPath(commentAttachment.Id, "commit"), new CommitAttachmentRequest(null, comment.Id), ApiTestContext.Json, CancellationToken)).EnsureSuccessStatusCode();

        var withoutComments = await Client.GetFromJsonAsync<List<AttachmentView>>($"/api/v1/orgs/work-items/items/{item.Key}/attachments", ApiTestContext.Json, CancellationToken);
        Assert.Collection(withoutComments!, attachment => Assert.Equal(itemAttachment.Id, attachment.Id));

        var withComments = await Client.GetFromJsonAsync<List<AttachmentView>>($"/api/v1/orgs/work-items/items/{item.Key}/attachments?include=comments", ApiTestContext.Json, CancellationToken);
        Assert.Equal([itemAttachment.Id, commentAttachment.Id], withComments!.Select(attachment => attachment.Id));
        Assert.Equal(comment.Id, withComments![1].CommentId);
    }

    [Fact]
    public async Task commit_refuses_an_attachment_whose_object_is_gone()
    {
        var attachment = await UploadOkAsync("notes.txt", "text/plain", "hello"u8.ToArray());
        var storage = Context.Factory.Services.GetRequiredService<IBlobStorage>();
        await storage.DeleteAsync(await ObjectKeyAsync(attachment.Id), CancellationToken);
        var item = await CreateAsync(WorkItemType.Bug, "Attachment owner");

        var commit = await Client.PostAsJsonAsync(AttachmentPath(attachment.Id, "commit"),
            new CommitAttachmentRequest(item.Id, null), ApiTestContext.Json, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(AttachmentPath(attachment.Id, "download"), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task pending_attachment_downloads_for_its_uploader_only()
    {
        var attachment = await UploadOkAsync("pasted.png", "image/png", Png(10, 10));

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(AttachmentPath(attachment.Id, "download"), CancellationToken)).StatusCode);

        await using (var connection = new NpgsqlConnection(Context.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var reassign = new NpgsqlCommand("UPDATE work.attachments SET uploaded_by = 'someone-else' WHERE id = @id", connection);
            reassign.Parameters.AddWithValue("id", attachment.Id);
            await reassign.ExecuteNonQueryAsync(CancellationToken);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(AttachmentPath(attachment.Id, "download"), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task orphan_sweeper_deletes_an_expired_pending_blob_and_metadata()
    {
        var id = Guid.CreateVersion7();
        var key = $"org/{Guid.CreateVersion7()}/project/{Guid.CreateVersion7()}/attachments/{id}/orphan.txt";
        var storage = Context.Factory.Services.GetRequiredService<IBlobStorage>();
        await storage.PutAsync(key, new MemoryStream("orphan"u8.ToArray()), "text/plain", CancellationToken);

        await using (var connection = new NpgsqlConnection(Context.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO work.attachments (id, organization_id, project_id, object_key, file_name, content_type, size_bytes, uploaded_by, status, created_at) VALUES (@id, @org, @project, @key, 'orphan.txt', 'text/plain', 6, 'uploader', 0, now() - interval '2 hours')", connection);
            insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("org", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("project", Guid.CreateVersion7()); insert.Parameters.AddWithValue("key", key);
            await insert.ExecuteNonQueryAsync(CancellationToken);
        }

        var service = new AttachmentCleanupService(Context.Factory.Services.GetRequiredService<IServiceScopeFactory>(), storage,
            Options.Create(new AttachmentsOptions { PendingLifetime = TimeSpan.FromHours(1) }), TimeProvider.System,
            NullLogger<AttachmentCleanupService>.Instance);
        await service.RunOnceAsync(CancellationToken);

        Assert.False(await storage.ExistsAsync(key, CancellationToken));
        await using var verify = new NpgsqlConnection(Context.ConnectionString);
        await verify.OpenAsync(CancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM work.attachments WHERE id = @id", verify);
        count.Parameters.AddWithValue("id", id);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(CancellationToken))!);
    }

    private string UploadPath => $"/api/v1/orgs/work-items/projects/{Project.Key}/attachments";
    private static string AttachmentPath(Guid id, string suffix) => $"/api/v1/orgs/work-items/attachments/{id}/{suffix}";

    private async Task<HttpResponseMessage> UploadAsync(string name, string type, byte[] payload)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return await Client.PostAsync(UploadPath, form, CancellationToken);
    }

    private async Task<AttachmentView> UploadOkAsync(string name, string type, byte[] payload)
    {
        var response = await UploadAsync(name, type, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AttachmentView>(ApiTestContext.Json, CancellationToken))!;
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private async Task<long> CountAttachmentsAsync()
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM work.attachments", connection);
        return (long)(await count.ExecuteScalarAsync(CancellationToken))!;
    }

    private async Task<string> ObjectKeyAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Context.ConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var key = new NpgsqlCommand("SELECT object_key FROM work.attachments WHERE id = @id", connection);
        key.Parameters.AddWithValue("id", id);
        return (string)(await key.ExecuteScalarAsync(CancellationToken))!;
    }
}
