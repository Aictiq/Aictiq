using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Aictiq.Modules.WorkItems.Contracts;
using Aictiq.SharedKernel.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Aictiq.Modules.Identity;
using Aictiq.Modules.WorkItems;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Domain;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Outbox;
using Aictiq.SharedKernel.Persistence;

namespace Aictiq.IntegrationTests.Outbox;

public sealed record TestExplodingEvent(string Message) : DomainEvent, IIntegrationEvent;
public sealed class TestExplodingHandler : IDomainEventHandler<TestExplodingEvent>
{ public Task HandleAsync(TestExplodingEvent domainEvent, CancellationToken cancellationToken) => throw new InvalidOperationException("deliberate handler failure"); }

/// <summary>The outbox is shared infrastructure, so it is exercised without retaining the deleted Notes sample.</summary>
public sealed class OutboxProcessorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private NpgsqlDataSource _dataSource = null!;
    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync("outbox_tests"); _dataSource = NpgsqlDataSource.Create(connection);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(_dataSource); services.AddSharedKernel(); services.AddSingleton<ICurrentUser, SystemCurrentUser>(); services.AddModuleDbContext<IdentityDbContext>("identity"); services.AddWorkItemsModule(); services.AddWorkItemsWorkers(); var configuration = new ConfigurationBuilder().Build(); services.AddSingleton<IConfiguration>(configuration); services.AddRealtimeNotifyPublisher(configuration); services.AddScoped<IDomainEventHandler<TestExplodingEvent>, TestExplodingHandler>(); services.AddOutboxProcessor(typeof(WorkItemsDbContext).Assembly, typeof(OutboxProcessorTests).Assembly);
        _provider = services.BuildServiceProvider(); await MigrationRunner.MigrateAsync(_provider, [typeof(IdentityDbContext), typeof(WorkItemsDbContext)]);
    }
    public async ValueTask DisposeAsync() { await _provider.DisposeAsync(); await _dataSource.DisposeAsync(); }
    [Fact]
    public async Task a_failing_handler_increments_attempts_and_keeps_the_message_pending()
    {
        var ct = TestContext.Current.CancellationToken; Guid messageId;
        using (var scope = _provider.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>(); var message = OutboxMessage.From(new TestExplodingEvent("boom")); messageId = message.Id; db.Set<OutboxMessage>().Add(message); await db.SaveChangesAsync(ct); }
        await _provider.GetRequiredService<OutboxProcessor>().ProcessPendingAsync(ct);
        using var verify = _provider.CreateScope(); var failed = await verify.ServiceProvider.GetRequiredService<WorkItemsDbContext>().Set<OutboxMessage>().SingleAsync(x => x.Id == messageId, ct); Assert.Null(failed.ProcessedAt); Assert.Equal(1, failed.Attempts);
    }

    /// <summary>
    /// ItemChanged is an integration event: Workers handle it, not the API. Its realtime push
    /// has to leave that process over the NOTIFY channel the API listens on, or no item edit
    /// ever reaches an open board.
    /// </summary>
    [Fact]
    public async Task an_item_change_handled_from_the_outbox_is_published_on_the_realtime_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid();
        await using var listener = await _dataSource.OpenConnectionAsync(ct);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, args) => received.TrySetResult(args.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {RealtimeBackplane.Channel}", listener)) await listen.ExecuteNonQueryAsync(ct);

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkItemsDbContext>();
            db.Set<OutboxMessage>().Add(OutboxMessage.From(new ItemChanged(Guid.NewGuid(), projectId, Guid.NewGuid(), "RT-1", "ada", ["title"])));
            await db.SaveChangesAsync(ct);
        }
        await _provider.GetRequiredService<OutboxProcessor>().ProcessPendingAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!received.Task.IsCompleted) await listener.WaitAsync(timeout.Token);
        var envelope = System.Text.Json.JsonSerializer.Deserialize<RealtimeEnvelope>(await received.Task, RealtimeBackplane.Json)!;
        Assert.Equal("item.changed", envelope.EventName);
        Assert.Equal(projectId, envelope.ProjectId);
        Assert.Equal("RT-1", envelope.Payload.GetProperty("key").GetString());
    }
}
