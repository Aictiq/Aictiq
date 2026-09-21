using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Wiki;

public sealed class WikiDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WikiDbContext>
{
    public WikiDbContext CreateDbContext(string[] args) => new(ModuleDbContextRegistration.CreateDesignTimeOptions<WikiDbContext>("wiki"));
}
