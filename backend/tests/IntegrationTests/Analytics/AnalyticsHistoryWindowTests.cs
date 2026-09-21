using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Analytics;
using Aictiq.Modules.Analytics.Domain;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Analytics;

/// <summary>
/// The plan's 365-day analytics window: a *query* bound, not a deletion. A report asked to
/// start further back is moved forward and says where it actually begins; the snapshots
/// behind that line are still in the database afterwards, because the entitlement narrows
/// what a report shows and never prunes the source.
/// </summary>
[Trait("Category", "Analytics")]
[Collection("postgres")]
public sealed class AnalyticsHistoryWindowTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    private const string Slug = "history-co";

    private ApiTestContext _context = null!;
    private HttpClient _client = null!;
    private Guid _organizationId;
    private ProjectView _project = null!;
    private Guid _stateId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    public async ValueTask InitializeAsync()
    {
        _context = await ApiTestContext.CreateAsync(postgres, garage, "analytics_window",
            // Hosted allowances only exist where there is something to be entitled to. The
            // operator's own retention stays at its 730-day default, so the plan is the
            // narrower of the two bounds and therefore the one under test.
            settings => settings["Billing:Mode"] = "saas");
        var auth = await _context.RegisterAsync($"history-{Guid.NewGuid():N}@test.local", "Hattie", "Story");
        _client = _context.ClientFor(auth);

        var organization = await _client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest("History", Slug, null, null), ApiTestContext.Json, Ct);
        organization.EnsureSuccessStatusCode();
        _organizationId = (await organization.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;

        var project = await _client.PostAsJsonAsync($"/api/v1/orgs/{Slug}/projects",
            new CreateProjectRequest("Reports", "REP", null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<ProjectView>(ApiTestContext.Json, Ct))!;

        var workflows = (await _client.GetFromJsonAsync<List<WorkflowView>>(
            $"/api/v1/orgs/{Slug}/projects/{_project.Key}/workflows/", ApiTestContext.Json, Ct))!;
        _stateId = workflows.Single(workflow => workflow.IsDefault).States.First().Id;

        // The Hosted entitlement, without a card: what a new hosted organization is born with.
        await ExecuteAsync($"""
            INSERT INTO billing.evaluations (id, organization_id, started_at, ends_at)
            VALUES (gen_random_uuid(), '{_organizationId}', now(), now() + interval '30 days')
            """);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task the_report_window_is_clamped_to_365_days_and_the_older_snapshots_are_still_there()
    {
        var earliest = Today.AddDays(-364);
        await SeedSnapshotsAsync(Today.AddDays(-500), earliest.AddDays(-1), earliest, Today.AddDays(-100));
        Assert.Equal(4, await ScalarAsync("SELECT count(*) FROM analytics.item_state_daily"));

        // The widest range the endpoint accepts still reaches a year and a day back.
        var flow = await FlowAsync(Today.AddDays(-366), Today);
        var days = Days(flow);

        Assert.Equal(earliest, HistoryFrom(flow));
        Assert.Equal(earliest, days[0]);
        Assert.DoesNotContain(days, day => day < earliest);
        Assert.Equal(365, days.Count);

        // Cycle time agrees — one window, defined in one place.
        Assert.Equal(earliest, HistoryFrom(await CycleTimeAsync(Today.AddDays(-366), Today)));

        // A window that lies entirely behind the line shows nothing and says why.
        var behind = await FlowAsync(Today.AddDays(-730), Today.AddDays(-365));
        Assert.Equal(earliest, HistoryFrom(behind));
        Assert.Empty(Days(behind));

        // And nothing was deleted to make any of that true.
        Assert.Equal(4, await ScalarAsync("SELECT count(*) FROM analytics.item_state_daily"));
        Assert.Equal(2, await ScalarAsync(
            $"SELECT count(*) FROM analytics.item_state_daily WHERE day < DATE '{earliest:yyyy-MM-dd}'"));
    }

    [Fact]
    public async Task a_range_wider_than_the_endpoint_allows_is_still_a_bad_request_not_a_silent_clamp()
    {
        // Two years in one request was never accepted; the entitlement did not change that,
        // and a caller asking for it gets told rather than quietly given 365 days.
        var response = await _client.GetAsync(
            $"/api/v1/orgs/{Slug}/projects/{_project.Key}/flow?from={Today.AddDays(-730):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------------------------- helpers

    private Task<JsonElement> FlowAsync(DateOnly from, DateOnly to) => ReportAsync("flow", from, to);

    private Task<JsonElement> CycleTimeAsync(DateOnly from, DateOnly to) => ReportAsync("cycle-time", from, to);

    /// <summary>
    /// Read as JSON rather than into the view records: what is under test is the wire
    /// contract a client reads (<c>historyFrom</c> and the days it is allowed to see).
    /// </summary>
    private async Task<JsonElement> ReportAsync(string report, DateOnly from, DateOnly to)
    {
        var response = await _client.GetAsync(
            $"/api/v1/orgs/{Slug}/projects/{_project.Key}/{report}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static DateOnly HistoryFrom(JsonElement report) =>
        DateOnly.ParseExact(report.GetProperty("historyFrom").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IReadOnlyList<DateOnly> Days(JsonElement flow) =>
        flow.GetProperty("days").EnumerateArray()
            .Select(day => DateOnly.ParseExact(day.GetProperty("day").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

    private async Task SeedSnapshotsAsync(params DateOnly[] days)
    {
        await using var scope = _context.Factory.Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<AmbientCurrentTenant>().Use(_organizationId);
        var analytics = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();
        foreach (var day in days)
        {
            analytics.ItemStateDaily.Add(new ItemStateDaily
            {
                OrganizationId = _organizationId,
                ItemId = Guid.CreateVersion7(),
                Day = day,
                ProjectId = _project.Id,
                StateId = _stateId,
                CapturedAt = DateTimeOffset.UtcNow,
            });
        }
        await analytics.SaveChangesAsync(Ct);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }
}
