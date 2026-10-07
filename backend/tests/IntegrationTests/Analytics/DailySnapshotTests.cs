using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Analytics.Workers;
using Aictiq.Modules.Tenancy.Domain;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.Modules.WorkItems.Domain;
using Aictiq.Modules.WorkItems.Endpoints;
using Aictiq.SharedKernel.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.IntegrationTests.Analytics;

/// <summary>
/// The daily state sample behind burndown, flow and velocity. Each organization's day is
/// its own, so the sweep first asks Tenancy for every organization's time zone - which it
/// once read from <c>settings -&gt;&gt; 'TimeZone'</c>, a key no row has, and then threw on
/// the null before writing a single sample for anyone.
/// </summary>
[Trait("Category", "Analytics")]
public sealed class DailySnapshotTests(PostgresFixture postgres, GarageFixture garage) : IAsyncLifetime
{
    /// <summary>Noon UTC: far from local midnight in every zone used here.</summary>
    private static readonly DateTimeOffset Now = new(2030, 1, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2030, 1, 7);

    private ApiTestContext _context = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The sweep is a Workers service, so the API host does not run one of its own.
        _context = await ApiTestContext.CreateAsync(postgres, garage, "analytics_daily");
        var auth = await _context.RegisterAsync($"daily-{Guid.NewGuid():N}@test.local", "Dana", "Daily");
        _client = _context.ClientFor(auth);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task the_time_zone_source_reads_the_zone_each_organization_stored()
    {
        var sarajevo = await CreateOrganizationAsync("sarajevo-co", "Europe/Sarajevo");
        var utc = await CreateOrganizationAsync("utc-co", null);

        // What is actually in the column, so the test cannot pass on a matching mistake.
        Assert.Equal("Europe/Sarajevo", await ScalarAsync<string>(
            $"SELECT settings ->> 'timeZone' FROM tenancy.organizations WHERE id = '{sarajevo}'"));

        await using var scope = _context.Factory.Services.CreateAsyncScope();
        var zones = (await scope.ServiceProvider.GetRequiredService<IOrganizationTimeZoneSource>().ListAsync(Ct))
            .ToDictionary(zone => zone.OrganizationId, zone => zone.TimeZone);

        Assert.Equal("Europe/Sarajevo", zones[sarajevo]);
        Assert.Equal("UTC", zones[utc]);
    }

    [Fact]
    public async Task a_missing_or_unknown_time_zone_is_utc_and_never_stops_the_other_organizations()
    {
        var sarajevo = await CreateOrganizationAsync("good-co", "Europe/Sarajevo");
        var unknown = await CreateOrganizationAsync("mars-co", null);
        var missing = await CreateOrganizationAsync("blank-co", null);
        foreach (var slug in new[] { "good-co", "mars-co", "blank-co" })
        {
            await CreateItemAsync(slug);
        }
        // Rows the API would refuse to write, but a database can still hold.
        await ExecuteAsync($$"""
            UPDATE tenancy.organizations SET settings = '{"timeZone": "Mars/Olympus_Mons", "weekStart": 1}' WHERE id = '{{unknown}}';
            UPDATE tenancy.organizations SET settings = '{"weekStart": 1}' WHERE id = '{{missing}}';
            """);

        await SweepAsync();

        foreach (var organization in new[] { sarajevo, unknown, missing })
        {
            Assert.Equal(1L, await ScalarAsync<long>(
                $"SELECT count(*) FROM analytics.item_state_daily WHERE organization_id = '{organization}' AND day = DATE '{Day:yyyy-MM-dd}'"));
        }

        // A second sweep on the same day adds nothing.
        await SweepAsync();
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM analytics.item_state_daily"));
    }

    // --------------------------------------------------------------------------- helpers

    private async Task SweepAsync()
    {
        var sweep = ActivatorUtilities.CreateInstance<DailySnapshotService>(
            _context.Factory.Services, new FixedClock(Now));
        await sweep.RunOnceAsync(Ct);
    }

    private async Task<Guid> CreateOrganizationAsync(string slug, string? timeZone)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orgs",
            new CreateOrganizationRequest(slug, slug, timeZone, null), ApiTestContext.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrganizationView>(ApiTestContext.Json, Ct))!.Id;
    }

    private async Task CreateItemAsync(string slug)
    {
        var key = slug[..3].ToUpperInvariant();
        var project = await _client.PostAsJsonAsync($"/api/v1/orgs/{slug}/projects",
            new CreateProjectRequest("Daily", key, null, ProjectVisibility.Organization, null, null),
            ApiTestContext.Json, Ct);
        project.EnsureSuccessStatusCode();
        var item = await _client.PostAsJsonAsync($"/api/v1/orgs/{slug}/projects/{key}/items/",
            new CreateWorkItemRequest(WorkItemType.Bug, "Sampled", null, null, null, null, null, null,
                null, null, null, null, null, null),
            ApiTestContext.Json, Ct);
        Assert.True(item.IsSuccessStatusCode, await item.Content.ReadAsStringAsync(Ct));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
