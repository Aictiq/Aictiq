using System.Net.Http.Json;
using Aictiq.IntegrationTests.Storage;
using Aictiq.Modules.Tenancy.Endpoints;
using Aictiq.SharedKernel.Authorization;

namespace Aictiq.IntegrationTests.Tenancy;

/// <summary>
/// First-run seeding: `docker compose up` should land on a usable application, not on an
/// empty state whose only escape is a form the operator has to find first.
/// </summary>
[Trait("Category", "Tenancy")]
public sealed class TenancySeedingTests(PostgresFixture postgres, GarageFixture garage)
{
    [Fact]
    public async Task the_seeded_administrator_owns_the_seeded_organization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(
            postgres, garage, "tenancy_seed",
            settings =>
            {
                settings["Seed:OrganizationName"] = "Aictiq HQ";
                settings["Seed:OrganizationTimeZone"] = "Europe/Sarajevo";
            });

        var mine = await context.Admin.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);
        var seeded = Assert.Single(mine!);

        Assert.Equal("Aictiq HQ", seeded.Name);
        Assert.Equal("aictiq-hq", seeded.Slug);
        Assert.Equal(OrgRole.Owner, seeded.Role);

        var detail = await context.Admin.GetFromJsonAsync<OrganizationView>($"/api/v1/orgs/{seeded.Slug}", ApiTestContext.Json, ct);
        Assert.Equal("Europe/Sarajevo", detail!.TimeZone);
    }

    [Fact]
    public async Task nothing_is_seeded_without_a_configured_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await ApiTestContext.CreateAsync(postgres, garage, "tenancy_noseed");

        var mine = await context.Admin.GetFromJsonAsync<List<OrganizationSummary>>("/api/v1/orgs", ApiTestContext.Json, ct);

        Assert.Empty(mine!);
    }
}
