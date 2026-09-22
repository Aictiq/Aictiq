using Aictiq.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace Aictiq.Modules.Billing;

public sealed class BillingDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) => new(ModuleDbContextRegistration.CreateDesignTimeOptions<BillingDbContext>("billing"));
}
