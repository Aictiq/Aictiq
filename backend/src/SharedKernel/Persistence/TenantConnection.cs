using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Aictiq.SharedKernel.Persistence;

public static class TenantConnection
{
    /// <summary>
    /// The context's connection, opened so that raw <see cref="NpgsqlCommand"/>s see the
    /// request's tenant. <see cref="TenantSessionInterceptor"/> sets <c>app.org_id</c> when
    /// EF opens a connection or runs a command; a command built by hand is not EF's, and a
    /// connection opened with <c>connection.OpenAsync()</c> never told the interceptor, so
    /// RLS answers every such query with no rows at all. EF closes it with the context.
    /// </summary>
    public static async Task<NpgsqlConnection> OpenTenantConnectionAsync(this DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        await database.OpenConnectionAsync(cancellationToken);
        return (NpgsqlConnection)database.GetDbConnection();
    }
}
