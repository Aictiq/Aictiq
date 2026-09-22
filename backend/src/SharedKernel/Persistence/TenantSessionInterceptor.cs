using System.Data.Common;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Aictiq.SharedKernel.Persistence;

/// <summary>
/// Gives PostgreSQL the same tenant that EF's query filter sees. RLS is deliberately
/// independent of that filter, so raw SQL cannot turn <c>IgnoreQueryFilters()</c> into a
/// cross-organization read.
/// </summary>
public sealed class TenantSessionInterceptor(AmbientCurrentTenant tenant, ICurrentUser currentUser) : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SetTenantAsync(connection, transaction: null, local: false, CancellationToken.None).GetAwaiter().GetResult();

    public async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetTenantAsync(connection, transaction: null, local: false, cancellationToken);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result)
    {
        SetTenantAsync(connection, result, local: true, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public async ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
        TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await SetTenantAsync(connection, result, local: true, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await SetForCommandAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        SetForCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await SetForCommandAsync(command, cancellationToken);
        return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result)
    {
        SetForCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        return base.ScalarExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await SetForCommandAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        SetForCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        return base.NonQueryExecuting(command, eventData, result);
    }

    private Task SetForCommandAsync(DbCommand command, CancellationToken cancellationToken) =>
        SetTenantAsync(command.Connection!, command.Transaction, command.Transaction is not null, cancellationToken);

    private async Task SetTenantAsync(DbConnection connection, DbTransaction? transaction, bool local,
        CancellationToken cancellationToken)
    {
        // Clearing is not cosmetic. Pooled connections can otherwise retain a previous
        // request's session setting; an unset tenant must be a database-level deny-all.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Clear every policy input for every command. Connection pooling otherwise lets
        // a prior request's tenant, user, or invitation capability leak into another.
        command.CommandText = """
            SELECT set_config('app.org_id', @organization_id, @is_local),
                   set_config('app.user_id', @user_id, @is_local),
                   set_config('app.invitation_token_hash', @invitation_token_hash, @is_local),
                   set_config('app.runner_token_hash', @runner_token_hash, @is_local)
            """;
        AddParameter(command, "organization_id", tenant.OrganizationId?.ToString() ?? "");
        AddParameter(command, "user_id", currentUser.UserId ?? "");
        AddParameter(command, "invitation_token_hash", tenant.InvitationTokenHash ?? "");
        AddParameter(command, "runner_token_hash", tenant.RunnerTokenHash ?? "");
        AddParameter(command, "is_local", local);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
