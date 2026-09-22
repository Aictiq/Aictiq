using Aictiq.Modules.Billing;
using Aictiq.Modules.Billing.Domain;

namespace Aictiq.IntegrationTests.Billing;

/// <summary>
/// The arithmetic of a bill, without a database: which seats are charged, which agents
/// come free, and what a downgrade would leave over its limits.
/// </summary>
[Trait("Category", "Billing")]
public sealed class SeatComputationTests
{
    private static readonly BillingPlan Starter = new()
    {
        Code = "starter", HumanSeatPrice = 9, IncludedAgentsPerHuman = 3,
        Limits = new PlanLimits(20, 5, 20, 10_737_418_240),
    };

    private static readonly BillingPlan Team = new()
    {
        Code = "team", HumanSeatPrice = 15, Limits = new PlanLimits(null, 25, null, 107_374_182_400),
    };

    [Theory]
    [InlineData(1, 0, 1, 0)]  // just the owner
    [InlineData(1, 3, 1, 0)]  // three agents ride free with one human
    [InlineData(1, 5, 1, 2)]  // two beyond the allowance are billed
    [InlineData(2, 5, 2, 0)]  // a second human brings three more free agents
    [InlineData(4, 13, 4, 1)]
    [InlineData(0, 2, 1, 0)]  // never a zero-seat subscription: there is always a human Owner
    public void starter_bills_every_human_and_only_agents_beyond_three_per_human(
        int humans, int agents, int billedHumans, int billedAgents)
    {
        Assert.Equal(new SeatQuantities(billedHumans, billedAgents), BillingUsage.Seats(humans, agents, Starter));
    }

    [Fact]
    public void a_plan_without_an_allowance_never_bills_agents_separately()
    {
        Assert.Equal(new SeatQuantities(2, 0), BillingUsage.Seats(2, 20, Team));
    }

    [Fact]
    public void lines_name_the_configured_prices_and_leave_out_a_zero_agent_line()
    {
        var stripe = new StripeOptions
        {
            Prices = new(StringComparer.OrdinalIgnoreCase) { ["starter_human"] = "price_h", ["starter_agent"] = "price_a" },
        };

        Assert.Equal([new("price_h", 3)], BillingUsage.Lines(stripe, "starter", new SeatQuantities(3, 0)));
        Assert.Equal([new("price_h", 1), new("price_a", 2)], BillingUsage.Lines(stripe, "starter", new SeatQuantities(1, 2)));
        // Agents to bill but no agent price configured: refuse rather than silently undercharge.
        Assert.Null(BillingUsage.Lines(new StripeOptions { Prices = new() { ["starter_human"] = "price_h" } },
            "starter", new SeatQuantities(1, 2)));
        Assert.Equal(("starter", "agent"), stripe.Resolve("price_a"));
        Assert.Null(stripe.Resolve("price_someone_elses"));
    }

    [Fact]
    public void a_downgrade_lists_every_limit_the_organization_is_over()
    {
        var usage = new OrganizationUsage("starter", Humans: 7, Agents: 1, Projects: 4, StorageBytes: 100);
        var free = new PlanLimits(5, 1, 3, 1_073_741_824);

        var exceeded = BillingUsage.Exceeded(usage, free);

        Assert.Equal([new ExceededLimit("seats_human", 7, 5), new ExceededLimit("projects", 4, 3)], exceeded);
        Assert.Empty(BillingUsage.Exceeded(usage, Team.Limits));
    }

    [Fact]
    public void saas_never_honours_the_self_hosted_default_and_self_hosted_ignores_what_is_stored()
    {
        Assert.Equal("free", PlanCodes.Effective("self_hosted", selfHosted: false));
        Assert.Equal("team", PlanCodes.Effective("team", selfHosted: false));
        Assert.Equal("self_hosted", PlanCodes.Effective("team", selfHosted: true));
    }
}
