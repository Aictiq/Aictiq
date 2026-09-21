using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Analytics;

public sealed class AnalyticsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args) => new(ModuleDbContextRegistration.CreateDesignTimeOptions<AnalyticsDbContext>("analytics"));
}
