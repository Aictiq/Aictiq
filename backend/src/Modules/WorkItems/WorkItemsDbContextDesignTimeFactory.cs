using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;
namespace Aictiq.Modules.WorkItems;
public sealed class WorkItemsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkItemsDbContext>
{ public WorkItemsDbContext CreateDbContext(string[] args) => new(ModuleDbContextRegistration.CreateDesignTimeOptions<WorkItemsDbContext>("work")); }
