using System.Collections.Concurrent;

namespace Aictiq.IntegrationTests;

/// <summary>
/// One API host per key - in practice per test class - instead of one per test. xUnit
/// builds the test class again for every test, so a host made in InitializeAsync is a
/// whole API start, database and sign-in each time; most tests only need a tenant of
/// their own, not a server of their own.
///
/// A class that shares a host must isolate its tests by data: a fresh organization slug,
/// fresh users. Tests inside one class still run one after another, so what a test leaves
/// behind is visible to the next - nothing may assert on a whole table.
///
/// Hosts live until the run ends. <see cref="PostgresFixture"/> disposes them before it
/// stops the container: xUnit gives assembly fixtures no order, and a host's disposal
/// still talks to Postgres (an app-role host drops its login).
/// </summary>
public static class SharedHosts
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ApiTestContext>>> Hosts = new();

    public static Task<ApiTestContext> GetAsync(string key, Func<Task<ApiTestContext>> create) =>
        Hosts.GetOrAdd(key, _ => new Lazy<Task<ApiTestContext>>(create)).Value;

    public static async ValueTask DisposeAllAsync()
    {
        foreach (var host in Hosts.Values.Where(host => host.IsValueCreated))
        {
            if (host.Value.IsCompletedSuccessfully)
            {
                await host.Value.Result.DisposeAsync();
            }
        }
        Hosts.Clear();
    }
}
