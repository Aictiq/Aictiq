using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Automation;
using Aictiq.Modules.Automation.Domain;
using Aictiq.Modules.Automation.Endpoints;
using Aictiq.Modules.Automation.Workers;
using Aictiq.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aictiq.IntegrationTests.Automation;

/// <summary>
/// Fleet mode on a hosted instance: only a paid organization may turn it on, a run that
/// requires tags goes only to a runner that has them, matching runners share the work by free
/// capacity without going over <c>maxParallel</c>, and a queue that waits too long calls the
/// customer's webhook, whose ephemeral runner enrolls, works and removes itself.
/// </summary>
[Trait("Category", "Automation")]
public sealed class FleetModeTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    protected override Action<IDictionary<string, string?>>? Configure => settings =>
    {
        settings["Billing:Mode"] = "saas";
        settings["Automation:PollTimeoutSeconds"] = "1";
        // Long enough that a busier runner never takes a run from a freer one by accident here.
        settings["Automation:FleetBalanceGraceSeconds"] = "300";
        settings["Automation:FleetWebhookAllowPrivateNetworks"] = "true";
    };

    private string FleetBase => $"/api/v1/orgs/{Slug}/fleet";

    [Fact]
    public async Task only_a_paid_hosted_organization_may_turn_fleet_mode_on()
    {
        // No subscription: offered as an upgrade, and refused.
        var status = await StatusAsync();
        Assert.Equal("upgrade", status.Availability);
        Assert.False(status.Active);
        var refused = await Owner.PutAsJsonAsync(FleetBase, new UpdateFleetRequest(true), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal(ProblemTypes.PlanLimit, await ProblemTypeAsync(refused));

        // An evaluation is entitled to Hosted, but has not paid for it.
        await SeedEvaluationAsync(10);
        Assert.Equal("upgrade", (await StatusAsync()).Availability);

        await SeedPaidSubscriptionAsync();
        Assert.Equal("available", (await StatusAsync()).Availability);
        var saved = await Owner.PutAsJsonAsync(FleetBase, new UpdateFleetRequest(true), ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var on = await StatusAsync();
        Assert.True(on.Active);
        Assert.False(on.Downgraded);
    }

    [Fact]
    public async Task a_run_that_requires_a_tag_goes_only_to_a_runner_that_has_it()
    {
        await EnableFleetAsync();
        await HelloAsync(Runner.Secret, tags: ["linux-docker"]);
        var gpu = await RegisterRunnerAsync("gpu-1");

        var run = await DispatchWithTagsAsync(ItemKey, ["GPU-enabled"]);
        Assert.Equal(["gpu-enabled"], run.RequiredTags);

        // gpu-1 has never said hello, and box-1 lacks the tag: nobody online can take it.
        var waiting = (await RunAsync(run.Id)).Waiting!;
        Assert.Equal("no-matching-runner", waiting.Reason);
        Assert.Equal(["gpu-enabled"], waiting.RequiredTags);

        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(box))!.StatusCode);

        // The tag set on Factory → Runners counts like one the CLI reports.
        var tagged = await Owner.PatchAsJsonAsync($"/api/v1/orgs/{Slug}/runners/{gpu.Runner.Id}",
            new UpdateRunnerRequest(null, null, ["gpu-enabled"]), ApiTestContext.Json, Ct);
        Assert.True(tagged.IsSuccessStatusCode, await tagged.Content.ReadAsStringAsync(Ct));
        var view = (await tagged.Content.ReadFromJsonAsync<RunnerView>(ApiTestContext.Json, Ct))!;
        Assert.Contains("gpu-enabled", view.EffectiveTags!);
        await HelloAsync(gpu.Secret);
        Assert.Contains("os:linux", (await RunnersAsync()).Single(r => r.Name == "gpu-1").EffectiveTags!);

        using var gpuClient = RunnerClient(gpu.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(gpuClient)).RunId);
    }

    [Fact]
    public async Task playbook_and_rule_tags_add_up_and_an_unknown_tag_is_reported_known_only_once_a_runner_has_it()
    {
        await EnableFleetAsync();
        var playbook = await Owner.PatchAsJsonAsync($"{ProjectBase}/playbooks/{PlaybookId}",
            new { requiredTags = new[] { "linux-docker" }, version = await PlaybookVersionAsync() }, ApiTestContext.Json, Ct);
        Assert.True(playbook.IsSuccessStatusCode, await playbook.Content.ReadAsStringAsync(Ct));

        var run = await DispatchWithTagsAsync(ItemKey, ["gpu-enabled"]);
        Assert.Equal(["gpu-enabled", "linux-docker"], run.RequiredTags);

        Assert.DoesNotContain("gpu-enabled", (await StatusAsync()).KnownTags);
        await HelloAsync(Runner.Secret, tags: ["gpu-enabled", "linux-docker"]);
        Assert.Contains("gpu-enabled", (await StatusAsync()).KnownTags);

        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);

        var invalid = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{(await CreateItemAsync("Bad tags")).Key}/runs",
            new DispatchRunRequest(null, null, RequiredTags: ["no spaces allowed"]), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task matching_runners_share_runs_by_free_capacity_and_never_go_over_max_parallel()
    {
        await EnableFleetAsync();
        var big = await RegisterRunnerAsync("big-1");
        await HelloAsync(Runner.Secret, maxParallel: 1);
        await HelloAsync(big.Secret, maxParallel: 3);
        using var small = RunnerClient(Runner.Secret);
        using var bigClient = RunnerClient(big.Secret);

        // box-1 has 1 free slot, big-1 has 3: box-1 leaves the run for big-1.
        var first = await DispatchAsync(ItemKey);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(small))!.StatusCode);
        Assert.Equal(first.Id, (await ClaimAsync(bigClient)).RunId);

        // big-1 now has 2 free, box-1 still 1: the next goes to big-1 as well.
        var second = await DispatchAsync((await CreateItemAsync("Second")).Key);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(small))!.StatusCode);
        Assert.Equal(second.Id, (await ClaimAsync(bigClient)).RunId);

        // 1 free each: a tie, and box-1 may take it.
        var third = await DispatchAsync((await CreateItemAsync("Third")).Key);
        Assert.Equal(third.Id, (await ClaimAsync(small)).RunId);

        // box-1 is full. A run past the balancing grace still never goes over its maxParallel,
        // even when it polls and nobody else could take it.
        var fourth = await DispatchAsync((await CreateItemAsync("Fourth")).Key);
        await ExecuteSqlAsync($"UPDATE automation.runs SET queued_at = now() - interval '1 hour' WHERE id = '{fourth.Id}'");
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(small))!.StatusCode);
        Assert.Equal(fourth.Id, (await ClaimAsync(bigClient)).RunId);

        var load = (await Owner.GetFromJsonAsync<RunnerLoadView>($"/api/v1/orgs/{Slug}/runners/load", ApiTestContext.Json, Ct))!;
        Assert.Equal(3, load.Runners.Single(r => r.RunnerId == big.Runner.Id).Running);
        Assert.Equal(1, load.Runners.Single(r => r.RunnerId == Runner.Runner.Id).Running);
    }

    [Fact]
    public async Task a_busier_runner_takes_a_run_once_the_freer_one_has_left_it_past_the_grace()
    {
        await EnableFleetAsync();
        var big = await RegisterRunnerAsync("big-1");
        await HelloAsync(Runner.Secret, maxParallel: 1);
        await HelloAsync(big.Secret, maxParallel: 3);
        var run = await DispatchAsync(ItemKey);
        await ExecuteSqlAsync($"UPDATE automation.runs SET queued_at = now() - interval '10 minutes' WHERE id = '{run.Id}'");

        using var small = RunnerClient(Runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(small)).RunId);
    }

    [Fact]
    public async Task after_a_downgrade_a_run_with_required_tags_falls_back_to_any_runner()
    {
        await EnableFleetAsync();
        await HelloAsync(Runner.Secret);
        var run = await DispatchWithTagsAsync(ItemKey, ["gpu-enabled"]);
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimMaybeAsync(box))!.StatusCode);

        await ExecuteSqlAsync($"UPDATE billing.subscriptions SET status = 6 WHERE organization_id = '{OrganizationId}'");
        var status = await StatusAsync();
        Assert.True(status.Downgraded);
        Assert.False(status.Active);

        Assert.Equal(run.Id, (await ClaimAsync(box)).RunId);
    }

    [Fact]
    public async Task a_queue_past_the_threshold_calls_the_webhook_once_per_cooldown_and_records_a_failure()
    {
        await EnableFleetAsync();
        using var receiver = new WebhookReceiver();
        var saved = await Owner.PutAsJsonAsync(FleetBase,
            new UpdateFleetRequest(null, "https://127.0.0.1/hook", 60, 600), ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
        var secret = (await saved.Content.ReadFromJsonAsync<FleetSettingsSaved>(ApiTestContext.Json, Ct))!.WebhookSecret!;
        // The receiver is plain HTTP on loopback; the URL check that requires HTTPS is not under test here.
        await ExecuteSqlAsync($"UPDATE automation.fleet_settings SET scale_webhook_url = '{receiver.Url}'");

        var run = await DispatchWithTagsAsync(ItemKey, ["gpu-enabled"]);
        // Not yet past the 60 s threshold: nothing is called.
        await SweepAsync();
        Assert.Empty(receiver.Requests);

        await ExecuteSqlAsync($"UPDATE automation.runs SET queued_at = now() - interval '5 minutes' WHERE id = '{run.Id}'");
        await SweepAsync();
        var call = Assert.Single(receiver.Requests);
        Assert.Equal(Sign(call.Body, secret), call.Signature);
        using (var payload = JsonDocument.Parse(call.Body))
        {
            Assert.Equal("fleet.scale_up", payload.RootElement.GetProperty("event").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("queue").GetProperty("waiting").GetInt32());
            var demand = Assert.Single(payload.RootElement.GetProperty("demand").EnumerateArray());
            Assert.Equal("gpu-enabled", Assert.Single(demand.GetProperty("requiredTags").EnumerateArray()).GetString());
        }

        // Inside the cooldown: not called again.
        await SweepAsync();
        Assert.Single(receiver.Requests);

        // Past it, a failing endpoint is called once, and the run says why it still waits.
        receiver.Status = HttpStatusCode.InternalServerError;
        await ExecuteSqlAsync("UPDATE automation.fleet_settings SET last_scale_call_at = now() - interval '1 hour'");
        await SweepAsync();
        Assert.Equal(2, receiver.Requests.Count);
        var scaleUp = (await RunAsync(run.Id)).Waiting!.ScaleUp!;
        Assert.Contains("HTTP 500", scaleUp.Error, StringComparison.Ordinal);
        await SweepAsync();
        Assert.Equal(2, receiver.Requests.Count);
    }

    [Fact]
    public async Task an_ephemeral_runner_enrolls_with_the_fleet_token_works_and_removes_itself()
    {
        using var anonymous = Context.Factory.CreateClient();
        await EnableFleetAsync();
        var minted = await Owner.PostAsync($"{FleetBase}/enroll-token", null, Ct);
        Assert.True(minted.IsSuccessStatusCode, await minted.Content.ReadAsStringAsync(Ct));
        var token = (await minted.Content.ReadFromJsonAsync<FleetSecretIssued>(ApiTestContext.Json, Ct))!.Secret;

        var wrong = await EnrollAsync(anonymous, token[..^1] + (token[^1] == 'a' ? 'b' : 'a'));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var enrolled = await EnrollAsync(anonymous, token, "k8s-pod-1", ["gpu-enabled"]);
        Assert.True(enrolled.IsSuccessStatusCode, await enrolled.Content.ReadAsStringAsync(Ct));
        var runner = (await enrolled.Content.ReadFromJsonAsync<RunnerEnrolledView>(ApiTestContext.Json, Ct))!;
        await HelloAsync(runner.Secret);
        var listed = (await RunnersAsync()).Single(r => r.Id == runner.RunnerId);
        Assert.True(listed.Ephemeral);
        Assert.Equal(["gpu-enabled"], listed.Tags);

        var run = await DispatchWithTagsAsync(ItemKey, ["gpu-enabled"]);
        using var pod = RunnerClient(runner.Secret);
        Assert.Equal(run.Id, (await ClaimAsync(pod)).RunId);

        Assert.Equal(HttpStatusCode.NoContent, (await pod.PostAsync("/api/v1/runner/deregister", null, Ct)).StatusCode);
        Assert.DoesNotContain(await RunnersAsync(), r => r.Id == runner.RunnerId);

        // A person's runner is not removed this way.
        using var box = RunnerClient(Runner.Secret);
        Assert.Equal(HttpStatusCode.Conflict, (await box.PostAsync("/api/v1/runner/deregister", null, Ct)).StatusCode);

        // One that died without removing itself is the sweeper's.
        var crashed = (await (await EnrollAsync(anonymous, token)).Content
            .ReadFromJsonAsync<RunnerEnrolledView>(ApiTestContext.Json, Ct))!;
        Assert.StartsWith("ephemeral-", crashed.Name, StringComparison.Ordinal);
        await ExecuteSqlAsync($"UPDATE automation.runners SET created_at = now() - interval '1 hour' WHERE id = '{crashed.RunnerId}'");
        await SweepAsync();
        Assert.DoesNotContain(await RunnersAsync(), r => r.Id == crashed.RunnerId);

        // Revoked, the token enrolls nobody.
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.DeleteAsync($"{FleetBase}/enroll-token", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrollAsync(anonymous, token)).StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private Task SeedPaidSubscriptionAsync() => ExecuteSqlAsync($"""
        INSERT INTO billing.subscriptions (id, organization_id, plan, stripe_customer_id, stripe_subscription_id, status,
            cancel_at_period_end, seats_human, seats_agent, created_at, updated_at)
        VALUES (gen_random_uuid(), '{OrganizationId}', 'hosted', 'cus_fleet', 'sub_fleet', 4, false, 1, 0, now(), now())
        """);

    private async Task EnableFleetAsync()
    {
        await SeedPaidSubscriptionAsync();
        var saved = await Owner.PutAsJsonAsync(FleetBase, new UpdateFleetRequest(true), ApiTestContext.Json, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));
    }

    private async Task<FleetStatusView> StatusAsync() =>
        (await Owner.GetFromJsonAsync<FleetStatusView>($"{FleetBase}/status", ApiTestContext.Json, Ct))!;

    private async Task<List<RunnerView>> RunnersAsync() =>
        (await Owner.GetFromJsonAsync<List<RunnerView>>($"/api/v1/orgs/{Slug}/runners", ApiTestContext.Json, Ct))!;

    private async Task<uint> PlaybookVersionAsync() =>
        (await Owner.GetFromJsonAsync<List<PlaybookView>>($"{ProjectBase}/playbooks", ApiTestContext.Json, Ct))!
            .Single(p => p.Id == PlaybookId).Version;

    private async Task HelloAsync(string secret, int maxParallel = 1, IReadOnlyList<string>? tags = null)
    {
        using var client = RunnerClient(secret);
        var capabilities = new RunnerCapabilities(1, [new RunnerHarness("claude", "2.1.0")], "linux", "x64", "0.8.0",
            maxParallel, Tags: tags);
        var response = await client.PostAsJsonAsync("/api/v1/runner/hello", new RunnerHelloRequest(capabilities), ApiTestContext.Json, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<RunView> DispatchWithTagsAsync(string itemKey, IReadOnlyList<string> tags)
    {
        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{itemKey}/runs",
            new DispatchRunRequest(null, null, RequiredTags: tags), ApiTestContext.Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
    }

    private Task<HttpResponseMessage> EnrollAsync(HttpClient client, string token, string? name = null, IReadOnlyList<string>? tags = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/runner/enroll")
        {
            Content = JsonContent.Create(new RunnerEnrollRequest(name, tags), options: ApiTestContext.Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request, Ct);
    }

    private async Task SweepAsync()
    {
        var services = Context.Factory.Services;
        var sweeper = new RunSweeper(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NpgsqlDataSource>(),
            services.GetRequiredService<IOptions<AutomationOptions>>(),
            TimeProvider.System,
            NullLogger<RunSweeper>.Instance);
        await sweeper.RunOnceAsync(Ct);
    }

    private static string Sign(string body, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>A loopback HTTP endpoint that records what it was sent and answers <see cref="Status"/>.</summary>
    private sealed class WebhookReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<(string Body, string? Signature)> _requests = [];

        public WebhookReceiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/hook/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.Accepted;

        public IReadOnlyList<(string Body, string? Signature)> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                lock (_requests) _requests.Add((body, context.Request.Headers["X-Aictiq-Signature"]));
                context.Response.StatusCode = (int)Status;
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}

/// <summary>Self-hosted installs are not offered fleet mode yet: the switch is hidden and the route answers as if it did not exist.</summary>
[Trait("Category", "Automation")]
public sealed class FleetModeSelfHostedTests(PostgresFixture postgres, GarageFixture garage) : RunTestBase(postgres, garage)
{
    [Fact]
    public async Task fleet_mode_is_hidden_and_dispatch_ignores_required_tags()
    {
        var status = (await Owner.GetFromJsonAsync<FleetStatusView>($"/api/v1/orgs/{Slug}/fleet/status", ApiTestContext.Json, Ct))!;
        Assert.Equal("hidden", status.Availability);
        var put = await Owner.PutAsJsonAsync($"/api/v1/orgs/{Slug}/fleet", new UpdateFleetRequest(true), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var response = await Owner.PostAsJsonAsync($"/api/v1/orgs/{Slug}/items/{ItemKey}/runs",
            new DispatchRunRequest(null, null, RequiredTags: ["gpu-enabled"]), ApiTestContext.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<RunView>(ApiTestContext.Json, Ct))!;
        Assert.Empty(run.RequiredTags!);
    }
}
