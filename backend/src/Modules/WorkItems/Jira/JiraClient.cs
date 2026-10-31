using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aictiq.Modules.WorkItems.Jira;

/// <summary>One Jira Cloud site and the account that reads it. The token lives in memory only.</summary>
public sealed record JiraCredentials(Uri Site, string Email, string ApiToken)
{
    /// <summary>Never the token: records print their members, and this one is passed around.</summary>
    public override string ToString() => $"{Email} @ {Site}";
}

public sealed record JiraProjectSummary(string Id, string Key, string Name);
public sealed record JiraStatus(string Name, string Category);
public sealed record JiraIssueType(string Name, bool Subtask, IReadOnlyList<JiraStatus> Statuses);
public sealed record JiraUser(string AccountId, string DisplayName, string? Email);
public sealed record JiraFieldIds(string? StoryPoints, string? Sprint, string? EpicLink);
public sealed record JiraIssuePage(IReadOnlyList<JsonElement> Issues, string? NextPageToken);
public sealed record JiraComment(string Id, JiraUser? Author, JsonElement Body, DateTimeOffset Created);

/// <summary>Jira answered with something other than what was asked for. The message is safe to show.</summary>
public class JiraException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>The email and token were refused, or the account cannot see what was asked for.</summary>
public sealed class JiraAuthException(string message, HttpStatusCode status) : JiraException(message, status);

/// <summary>
/// A small Jira Cloud REST v3 client for the one-time import: enough to check a connection, list
/// what an account can see and read a project's issues, comments and attachments.
///
/// Every request goes through <see cref="HttpClientName"/>, whose handler refuses non-public
/// addresses at connect time and never follows a redirect on its own. Attachment downloads do
/// follow Jira's redirect to its media host, by hand, and drop the credentials when they leave
/// the site. Rate limiting (429) and brief outages (502-504) are retried with Jira's Retry-After.
/// </summary>
public sealed class JiraClient(IHttpClientFactory clients)
{
    public const string HttpClientName = "aictiq-jira";
    private const int MaxAttempts = 5;
    private const int MaxRedirects = 5;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The site part of what the user typed, as <c>https://host</c>. Jira Cloud is reached at the
    /// site root, so a pasted board or issue URL is accepted and its path dropped.
    /// </summary>
    public static bool TryNormalizeSite(string? value, JiraImportOptions policy, out Uri site, out string error)
    {
        site = null!; error = "";
        var text = value?.Trim() ?? "";
        if (text.Length == 0) { error = "Enter your Jira site URL, for example https://your-team.atlassian.net."; return false; }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (text.Length > 255 || !Uri.TryCreate(text, UriKind.Absolute, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
        { error = "That is not a valid Jira site URL."; return false; }
        if (parsed.Scheme != Uri.UriSchemeHttps && !(policy.AllowInsecureSites && parsed.Scheme == Uri.UriSchemeHttp))
        { error = "The Jira site URL must start with https://."; return false; }
        var host = parsed.IdnHost.ToLowerInvariant();
        if (!policy.AllowInsecureSites && policy.AllowedSiteSuffixes.Length > 0 &&
            !policy.AllowedSiteSuffixes.Any(suffix => host.EndsWith(suffix.ToLowerInvariant(), StringComparison.Ordinal) && host.Length > suffix.Length))
        { error = $"Only Jira Cloud sites ({string.Join(", ", policy.AllowedSiteSuffixes.Select(s => "*" + s))}) can be imported from."; return false; }
        site = new Uri(parsed.IsDefaultPort ? $"{parsed.Scheme}://{host}" : $"{parsed.Scheme}://{host}:{parsed.Port}");
        return true;
    }

    public async Task<JiraUser> MyselfAsync(JiraCredentials credentials, CancellationToken ct)
    {
        using var document = await GetJsonAsync(credentials, "rest/api/3/myself", ct);
        return User(document.RootElement) ?? throw new JiraException("Jira did not say who this account is.");
    }

    public async Task<IReadOnlyList<JiraProjectSummary>> ProjectsAsync(JiraCredentials credentials, CancellationToken ct)
    {
        var projects = new List<JiraProjectSummary>();
        for (var startAt = 0; projects.Count < 5000; )
        {
            using var document = await GetJsonAsync(credentials, $"rest/api/3/project/search?startAt={startAt}&maxResults=50&orderBy=name", ct);
            var values = document.RootElement.GetProperty("values");
            foreach (var project in values.EnumerateArray()) projects.Add(Project(project));
            if (values.GetArrayLength() == 0 || Bool(document.RootElement, "isLast") != false) break;
            startAt += values.GetArrayLength();
        }
        return projects;
    }

    public async Task<JiraProjectSummary?> ProjectAsync(JiraCredentials credentials, string key, CancellationToken ct)
    {
        try
        {
            using var document = await GetJsonAsync(credentials, $"rest/api/3/project/{Uri.EscapeDataString(key)}", ct);
            return Project(document.RootElement);
        }
        catch (JiraException exception) when (exception.Status == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>The project's issue types, each with the statuses its workflow can reach.</summary>
    public async Task<IReadOnlyList<JiraIssueType>> IssueTypesAsync(JiraCredentials credentials, string projectKey, CancellationToken ct)
    {
        using var document = await GetJsonAsync(credentials, $"rest/api/3/project/{Uri.EscapeDataString(projectKey)}/statuses", ct);
        return document.RootElement.EnumerateArray().Select(type => new JiraIssueType(
            Text(type, "name") ?? "Task", Bool(type, "subtask") == true,
            type.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array
                ? statuses.EnumerateArray().Select(Status).ToList() : [])).ToList();
    }

    public async Task<int> CountAsync(JiraCredentials credentials, string jql, CancellationToken ct)
    {
        using var document = await SendJsonAsync(credentials, HttpMethod.Post, "rest/api/3/search/approximate-count", new { jql }, ct);
        return document.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out var value) ? value : 0;
    }

    /// <summary>Story points, sprint and epic link are custom fields whose ids differ per site.</summary>
    public async Task<JiraFieldIds> FieldIdsAsync(JiraCredentials credentials, CancellationToken ct)
    {
        using var document = await GetJsonAsync(credentials, "rest/api/3/field", ct);
        var fields = document.RootElement.EnumerateArray().Select(field => (
            Id: Text(field, "id") ?? "", Name: Text(field, "name") ?? "",
            Custom: field.TryGetProperty("schema", out var schema) ? Text(schema, "custom") ?? "" : "")).ToList();
        string? ByCustom(string custom) => fields.FirstOrDefault(f => f.Custom == custom).Id is { Length: > 0 } id ? id : null;
        string? ByName(params string[] names) => names.Select(name => fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id).FirstOrDefault(id => !string.IsNullOrEmpty(id));
        return new JiraFieldIds(
            ByName("Story Points", "Story point estimate"),
            ByCustom("com.pyxis.greenhopper.jira:gh-sprint") ?? ByName("Sprint"),
            ByCustom("com.pyxis.greenhopper.jira:gh-epic-link") ?? ByName("Epic Link"));
    }

    /// <summary>People who can be assigned issues in the project: the users the preview matches.</summary>
    public async Task<IReadOnlyList<JiraUser>> AssignableUsersAsync(JiraCredentials credentials, string projectKey, CancellationToken ct)
    {
        var users = new List<JiraUser>();
        for (var startAt = 0; users.Count < 5000; )
        {
            using var document = await GetJsonAsync(credentials, $"rest/api/3/user/assignable/search?project={Uri.EscapeDataString(projectKey)}&startAt={startAt}&maxResults=100", ct);
            var page = document.RootElement.EnumerateArray().Select(User).OfType<JiraUser>().ToList();
            users.AddRange(page.Where(u => users.All(existing => existing.AccountId != u.AccountId)));
            if (page.Count < 100) break;
            startAt += page.Count;
        }
        return users;
    }

    /// <summary>One page of issues through the enhanced search, which pages by token rather than offset.</summary>
    public async Task<JiraIssuePage> SearchAsync(JiraCredentials credentials, string jql, IReadOnlyList<string> fields, string? nextPageToken, CancellationToken ct)
    {
        using var document = await SendJsonAsync(credentials, HttpMethod.Post, "rest/api/3/search/jql",
            new { jql, fields, maxResults = 100, nextPageToken }, ct);
        var root = document.RootElement;
        var issues = root.TryGetProperty("issues", out var list) ? list.EnumerateArray().Select(x => x.Clone()).ToList() : [];
        var next = Bool(root, "isLast") == true ? null : Text(root, "nextPageToken");
        return new JiraIssuePage(issues, issues.Count == 0 ? null : next);
    }

    public async Task<IReadOnlyList<JiraComment>> CommentsAsync(JiraCredentials credentials, string issueKey, CancellationToken ct)
    {
        var comments = new List<JiraComment>();
        for (var startAt = 0; comments.Count < 10_000; )
        {
            using var document = await GetJsonAsync(credentials, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment?startAt={startAt}&maxResults=100&orderBy=created", ct);
            var page = document.RootElement.TryGetProperty("comments", out var list) ? list.EnumerateArray().ToList() : [];
            foreach (var comment in page)
                comments.Add(new JiraComment(Text(comment, "id") ?? "", comment.TryGetProperty("author", out var author) ? User(author) : null,
                    comment.TryGetProperty("body", out var body) ? body.Clone() : default, Date(comment, "created") ?? DateTimeOffset.UnixEpoch));
            var total = document.RootElement.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : comments.Count;
            if (page.Count == 0 || comments.Count >= total) break;
            startAt += page.Count;
        }
        return comments.OrderBy(x => x.Created).ToList();
    }

    public abstract record Download;
    public sealed record Downloaded(byte[] Bytes, string? ContentType) : Download;
    public sealed record TooLarge : Download;

    /// <summary>
    /// An attachment's bytes, read no further than <paramref name="maxBytes"/>. Jira answers the
    /// content URL with a redirect to its media service; the credentials go to the site only.
    /// </summary>
    public async Task<Download> DownloadAsync(JiraCredentials credentials, Uri content, long maxBytes, CancellationToken ct)
    {
        var url = content;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!SameSite(url, credentials.Site) && url.Scheme != Uri.UriSchemeHttps) throw new JiraException("Jira redirected the download to an insecure address.");
            using var response = await SendAsync(credentials, () => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            await EnsureSuccessAsync(response, ct);
            if (response.Content.Headers.ContentLength is { } length && length > maxBytes) return new TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) return new TooLarge();
                buffer.Write(chunk, 0, read);
            }
            return new Downloaded(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        throw new JiraException("Jira redirected the download too many times.");
    }

    private async Task<JsonDocument> GetJsonAsync(JiraCredentials credentials, string path, CancellationToken ct) =>
        await SendJsonAsync(credentials, HttpMethod.Get, path, null, ct);

    private async Task<JsonDocument> SendJsonAsync(JiraCredentials credentials, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = new Uri(credentials.Site, path);
        using var response = await SendAsync(credentials, () =>
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            return request;
        }, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400) throw new JiraException("Jira redirected the request. Check that the site URL is your Jira Cloud address.", response.StatusCode);
        await EnsureSuccessAsync(response, ct);
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException) { throw new JiraException("Jira answered with something that is not JSON. Check that the site URL is your Jira Cloud address."); }
    }

    private async Task<HttpResponseMessage> SendAsync(JiraCredentials credentials, Func<HttpRequestMessage> create, HttpCompletionOption completion, CancellationToken ct)
    {
        var client = clients.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (SameSite(request.RequestUri!, credentials.Site))
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Email}:{credentials.ApiToken}")));
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, completion, ct); }
            catch (HttpRequestException) { throw new JiraException($"Could not reach Jira at {credentials.Site.Host}. Check the site URL."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new JiraException($"Jira at {credentials.Site.Host} did not answer in time."); }
            var retry = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (!retry || attempt >= MaxAttempts) return response;
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 60)), ct);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new JiraAuthException("Jira did not accept this email and API token.", response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new JiraAuthException("This Jira account is not allowed to read that.", response.StatusCode);
        // Jira's own words, when it gives some, are the useful part ("The value 'X' does not exist for the field 'project'.").
        var detail = "";
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.TryGetProperty("errorMessages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                detail = string.Join(" ", messages.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (JsonException) { }
        detail = detail.Length > 300 ? detail[..300] : detail;
        throw new JiraException(detail.Length > 0 ? $"Jira answered {(int)response.StatusCode}: {detail}" : $"Jira answered {(int)response.StatusCode}.", response.StatusCode);
    }

    private static bool SameSite(Uri url, Uri site) => string.Equals(url.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(url.IdnHost, site.IdnHost, StringComparison.OrdinalIgnoreCase) && url.Port == site.Port;

    private static JiraProjectSummary Project(JsonElement x) => new(Text(x, "id") ?? "", Text(x, "key") ?? "", Text(x, "name") ?? Text(x, "key") ?? "");
    private static JiraStatus Status(JsonElement x) => new(Text(x, "name") ?? "", x.TryGetProperty("statusCategory", out var category) ? Text(category, "key") ?? "unknown" : "unknown");

    internal static JiraUser? User(JsonElement x) => x.ValueKind == JsonValueKind.Object && Text(x, "accountId") is { } id
        ? new JiraUser(id, Text(x, "displayName") ?? "Unknown Jira user", Text(x, "emailAddress")) : null;

    internal static string? Text(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    internal static bool? Bool(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Jira writes offsets without a colon (<c>2024-01-15T10:20:30.000+0100</c>), which .NET does not read as-is.</summary>
    internal static DateTimeOffset? Date(JsonElement x, string name) => ParseDate(Text(x, name));

    internal static DateTimeOffset? ParseDate(string? text) =>
        text is not null && DateTimeOffset.TryParse(Regex.Replace(text.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
}
