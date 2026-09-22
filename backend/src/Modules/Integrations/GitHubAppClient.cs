using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Aictiq.Modules.Integrations;

public sealed record GitHubAccount(string Login, string Type);
public sealed record GitHubRepository(long Id, string FullName, long InstallationId);
public sealed record GitHubBranch(string Url, bool Created);

/// <summary>
/// Minimal GitHub App REST client. Installation tokens are deliberately cached below
/// their one-hour GitHub lifetime, so a cache hit can never hand callers an expired token.
/// </summary>
public sealed class GitHubAppClient(
    IHttpClientFactory clients,
    HybridCache cache,
    IOptions<GitHubOptions> options,
    TimeProvider clock)
{
    private static readonly HybridCacheEntryOptions CacheForFiftyMinutes = new()
    {
        Expiration = TimeSpan.FromMinutes(50),
        LocalCacheExpiration = TimeSpan.FromMinutes(50),
    };

    public async Task<GitHubAccount> GetInstallationAsync(long installationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"app/installations/{installationId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
        using var response = await clients.CreateClient(IntegrationsModule.GitHubHttpClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var account = document.RootElement.GetProperty("account");
        return new GitHubAccount(account.GetProperty("login").GetString()!, account.GetProperty("type").GetString()!);
    }

    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(long installationId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            $"github:installation:{installationId}:repositories",
            (client: this, installationId),
            static async (state, ct) => await state.client.ListRepositoriesUncachedAsync(state.installationId, ct),
            CacheForFiftyMinutes,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesUncachedAsync(long installationId, CancellationToken cancellationToken)
    {
        var token = await InstallationTokenAsync(installationId, cancellationToken);
        var result = new List<GitHubRepository>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"installation/repositories?per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(IntegrationsModule.GitHubHttpClientName).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var repositories = document.RootElement.GetProperty("repositories");
            foreach (var repository in repositories.EnumerateArray())
            {
                result.Add(new GitHubRepository(repository.GetProperty("id").GetInt64(),
                    repository.GetProperty("full_name").GetString()!, installationId));
            }
            if (repositories.GetArrayLength() < 100) break;
        }
        return result;
    }

    private async Task<string> InstallationTokenAsync(long installationId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            $"github:installation:{installationId}:token",
            (client: this, installationId),
            static async (state, ct) => await state.client.CreateInstallationTokenAsync(state.installationId, ct),
            CacheForFiftyMinutes,
            cancellationToken: cancellationToken);

    /// <summary>
    /// The token a runner clones and pushes with. Same cache as every other
    /// caller: one installation token lives an hour, and minting a fresh one per request
    /// would be both slower and the kind of credential sprawl a run never needs.
    /// </summary>
    public Task<string> GetInstallationTokenAsync(long installationId, CancellationToken cancellationToken) =>
        InstallationTokenAsync(installationId, cancellationToken);

    private async Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateAppJwt());
        using var response = await clients.CreateClient(IntegrationsModule.GitHubHttpClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub did not return an installation token.");
    }

    /// <summary>Creates a branch from the repository default branch.  GitHub returning the
    /// existing-reference conflict is intentionally a successful retry: the local link is
    /// still written by the caller, closing the crash window after a remote create.</summary>
    public async Task<GitHubBranch> CreateDefaultBranchAsync(long installationId, string fullName, string branchName,
        CancellationToken cancellationToken)
    {
        var token = await InstallationTokenAsync(installationId, cancellationToken);
        var http = clients.CreateClient(IntegrationsModule.GitHubHttpClientName);
        using var repositoryRequest = Authorized(HttpMethod.Get, $"repos/{fullName}", token);
        using var repositoryResponse = await http.SendAsync(repositoryRequest, cancellationToken);
        repositoryResponse.EnsureSuccessStatusCode();
        using var repository = JsonDocument.Parse(await repositoryResponse.Content.ReadAsStreamAsync(cancellationToken));
        var defaultBranch = repository.RootElement.GetProperty("default_branch").GetString()!;
        var htmlUrl = repository.RootElement.GetProperty("html_url").GetString()!;
        using var referenceRequest = Authorized(HttpMethod.Get, $"repos/{fullName}/git/ref/heads/{Uri.EscapeDataString(defaultBranch)}", token);
        using var referenceResponse = await http.SendAsync(referenceRequest, cancellationToken);
        referenceResponse.EnsureSuccessStatusCode();
        using var reference = JsonDocument.Parse(await referenceResponse.Content.ReadAsStreamAsync(cancellationToken));
        var sha = reference.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        using var createRequest = Authorized(HttpMethod.Post, $"repos/{fullName}/git/refs", token);
        createRequest.Content = JsonContent.Create(new { @ref = $"refs/heads/{branchName}", sha });
        using var createResponse = await http.SendAsync(createRequest, cancellationToken);
        var created = createResponse.IsSuccessStatusCode;
        if (!created && createResponse.StatusCode != System.Net.HttpStatusCode.UnprocessableEntity) createResponse.EnsureSuccessStatusCode();
        return new GitHubBranch($"{htmlUrl}/tree/{Uri.EscapeDataString(branchName)}", created);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string CreateAppJwt()
    {
        var configured = options.Value;
        if (!configured.IsConfigured || !long.TryParse(configured.AppId, out _))
            throw new InvalidOperationException("GitHub App is not configured.");

        var now = clock.GetUtcNow();
        var header = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
        var payload = Base64Url(JsonSerializer.Serialize(new
        {
            iat = now.AddMinutes(-1).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = configured.AppId,
        }));
        var unsigned = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(configured.PrivateKey!);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
