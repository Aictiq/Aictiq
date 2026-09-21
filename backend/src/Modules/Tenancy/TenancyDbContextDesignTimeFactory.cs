using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Tenancy;

public sealed class TenancyDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.CreateDesignTimeOptions<TenancyDbContext>("tenancy"));
}
