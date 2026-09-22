using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace Aictiq.Modules.Automation;

public sealed class AutomationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AutomationDbContext>
{
    public AutomationDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.CreateDesignTimeOptions<AutomationDbContext>(AutomationDbContext.SchemaName));
}
