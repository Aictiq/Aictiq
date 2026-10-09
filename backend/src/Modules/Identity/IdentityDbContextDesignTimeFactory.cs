using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Identity;

public sealed class IdentityDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    /// <summary>
    /// Identity builds its model from <c>IdentityOptions.Stores.SchemaVersion</c>, read from
    /// the application's services. The design-time context has none, and without this it
    /// would model schema version 1 - and the next migration would drop the passkey table.
    /// </summary>
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection()
            .Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentityModule.SchemaVersion)
            .BuildServiceProvider();

        var options = new DbContextOptionsBuilder<IdentityDbContext>(
                ModuleDbContextRegistration.CreateDesignTimeOptions<IdentityDbContext>("identity"))
            .UseApplicationServiceProvider(services)
            .Options;

        return new IdentityDbContext(options);
    }
}
