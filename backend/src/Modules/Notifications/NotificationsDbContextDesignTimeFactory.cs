using Microsoft.EntityFrameworkCore.Design;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.Modules.Notifications;

public sealed class NotificationsDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.CreateDesignTimeOptions<NotificationsDbContext>("notify"));
}
