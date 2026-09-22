using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Aictiq.IntegrationTests;

/// <summary>
/// Counts the SQL commands EF Core executes inside the API under test, through a
/// DbCommandInterceptor that ApiTestContext wires into every module context when a test
/// asks for countQueries. An ILoggerProvider cannot do this job: the API boots Serilog,
/// whose AddSerilog replaces the host's ILoggerFactory, so a provider registered in DI
/// never sees EF's command events. Raw NpgsqlCommands - the full-text search endpoint's -
/// bypass EF entirely and are invisible to this counter by construction.
/// </summary>
public sealed class QueryCounter
{
    private const int CommandTextLength = 200;

    private int _count;
    private readonly ConcurrentQueue<string> _commands = new();

    public DbCommandInterceptor Interceptor { get; }

    public QueryCounter() => Interceptor = new CountingInterceptor(this);

    public int Count => Volatile.Read(ref _count);
    public IReadOnlyList<string> Commands => [.. _commands];

    public void Reset()
    {
        Volatile.Write(ref _count, 0);
        _commands.Clear();
    }

    /// <summary>The captured command texts, for the failure message of a broken bound.</summary>
    public string Describe() => Commands.Count == 0
        ? $"{Count} queries (none captured)"
        : $"{Count} queries:\n{string.Join("\n", Commands.Select((command, index) => $"  {index + 1}. {command}"))}";

    private void OnExecuted(DbCommand command)
    {
        Interlocked.Increment(ref _count);
        var text = command.CommandText;
        _commands.Enqueue(text.Length <= CommandTextLength ? text : text[..CommandTextLength] + "…");
    }

    private sealed class CountingInterceptor(QueryCounter counter) : DbCommandInterceptor
    {
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            counter.OnExecuted(command);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            counter.OnExecuted(command);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
        {
            counter.OnExecuted(command);
            return base.ScalarExecuted(command, eventData, result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            counter.OnExecuted(command);
            return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            counter.OnExecuted(command);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            counter.OnExecuted(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}

/// <summary>Appends the counting interceptor to one module context's options through DI.</summary>
public sealed class QueryCountingOptions<TContext>(QueryCounter counter) : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext
{
    public void Configure(IServiceProvider applicationServiceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(counter.Interceptor);
}

/// <summary>Locks the measured query counts of the hot read paths in with bounds.</summary>
public static class QueryCount
{
    /// <summary>Starts (or restarts) a measurement window on a counting context.</summary>
    public static QueryCounter StartQueryCount(this ApiTestContext context)
    {
        var counter = context.QueryCount
            ?? throw new InvalidOperationException("Create this context with countQueries: true to count queries.");
        counter.Reset();
        return counter;
    }

    public static void AssertAtMost(this QueryCounter counter, int max, string because) =>
        Assert.True(counter.Count <= max,
            $"Expected at most {max} queries {because}, observed {counter.Count}.\n{counter.Describe()}");

    public static void AssertExactly(this QueryCounter counter, int expected, string because)
    {
        if (counter.Count != expected)
            Assert.Fail($"Expected exactly {expected} queries {because}, observed {counter.Count}.\n{counter.Describe()}");
    }

    /// <summary>
    /// The other half of a page-size comparison: the same request with a bigger result must
    /// issue the same number of queries as the one measured before it.
    /// </summary>
    public static void AssertSameAs(this QueryCounter counter, int previousCount, string because)
    {
        if (counter.Count != previousCount)
            Assert.Fail($"Expected the same query count {because}: the first request issued {previousCount}, this one issued {counter.Count}.\n{counter.Describe()}");
    }
}
