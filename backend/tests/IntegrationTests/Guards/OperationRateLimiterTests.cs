using Microsoft.Extensions.Configuration;
using Aictiq.SharedKernel.Http;

namespace Aictiq.IntegrationTests.Guards;

public sealed class OperationRateLimiterTests
{
    [Fact]
    public void upload_budget_is_partitioned_by_authenticated_user_and_organization()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:UploadPermitLimitPerMinute"] = "2",
            ["RateLimiting:SearchPermitLimitPerMinute"] = "3"
        }).Build();
        var limiter = new OperationRateLimiter(configuration, TimeProvider.System);
        var firstOrganization = Guid.CreateVersion7();
        var secondOrganization = Guid.CreateVersion7();

        Assert.Null(limiter.RetryAfterSeconds(OperationRateLimiter.Uploads, "ada", firstOrganization, null));
        Assert.Null(limiter.RetryAfterSeconds(OperationRateLimiter.Uploads, "ada", firstOrganization, null));
        Assert.InRange(limiter.RetryAfterSeconds(OperationRateLimiter.Uploads, "ada", firstOrganization, null)!.Value, 1, 60);

        Assert.Null(limiter.RetryAfterSeconds(OperationRateLimiter.Uploads, "grace", firstOrganization, null));
        Assert.Null(limiter.RetryAfterSeconds(OperationRateLimiter.Uploads, "ada", secondOrganization, null));
        Assert.Null(limiter.RetryAfterSeconds(OperationRateLimiter.Search, "ada", firstOrganization, null));
    }
}
