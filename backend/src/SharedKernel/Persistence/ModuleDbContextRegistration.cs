using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aictiq.SharedKernel.Persistence;

public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers a module DbContext on the shared NpgsqlDataSource with snake_case
    /// naming, a per-schema migrations history table, and the auditing interceptor.
    /// </summary>
    // Constraint is DbContext (not ModuleDbContext) because the Identity context
    // inherits IdentityDbContext and shares the plumbing via ModuleDbContextSupport.
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : DbContext =>
        services.AddDbContext<TContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                sp.GetRequiredService<AuditingInterceptor>(),
                sp.GetRequiredService<TenantSessionInterceptor>()));

    /// <summary>Options for dotnet-ef design-time factories - must mirror the runtime options.</summary>
    public static DbContextOptions<TContext> CreateDesignTimeOptions<TContext>(string schema)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=localhost;Database=aictiq_design",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention()
            .Options;
}
