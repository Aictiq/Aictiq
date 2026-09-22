using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Integrations;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> run without a host, like every other module. Its
/// absence is why this module's migrations were hand-written without the <c>[Migration]</c>
/// and <c>[DbContext]</c> attributes EF discovers them by - and therefore never applied.
/// </summary>
public sealed class IntegrationsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
{
    public IntegrationsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.CreateDesignTimeOptions<IntegrationsDbContext>("integrations"));
}
