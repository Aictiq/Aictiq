using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Identity;

public sealed class IdentityDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.CreateDesignTimeOptions<IdentityDbContext>("identity"));
}
