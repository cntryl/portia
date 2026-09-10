using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Cntryl.Portia.Consumer;

public sealed class FleetBrokerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyScaleOutThenDepartureOrCrashRestoresAllPartitions(bool crash)
    {
        await using var first = await ConsumerBroker.ConnectAsync();
        var second = await ConsumerBroker.ConnectAsync();
        var realm = "fleet-" + Guid.NewGuid().ToString("N");
        var partitions = Enumerable.Range(0, 8).Select(i => $"lease://{realm}/parts/{i}").ToArray();
        var workerB = Enumerable.Range(0, 100).Select(i => "b" + i).First(worker =>
        {
            var count = partitions.Count(route => Owner(route, "a", worker) == worker);
            return count > 0 && count < partitions.Length;
        });
        var options = new FleetRunOptions
        {
            MembershipSelector = $"lease://{realm}/members/*",
            WorkerId = "a",
            LeaseTtl = TimeSpan.FromSeconds(2),
            ReconciliationInterval = TimeSpan.FromMilliseconds(100)
        };
        var activeA = new ConcurrentDictionary<string, bool>();
        var activeB = new ConcurrentDictionary<string, bool>();
        var startsA = new ConcurrentDictionary<string, int>();
        using var cancelA = new CancellationTokenSource();
        using var cancelB = new CancellationTokenSource();
        var runnerA = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(first.Lease),
            new FitzFleetMembership(first.Lease));
        var runnerB = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(second.Lease),
            new FitzFleetMembership(second.Lease));
        var runA = runnerA.RunAsync(partitions, (route, ct) => Hold(route, activeA, startsA, ct), options,
            cancelA.Token);
        Task? runB = null;
        try
        {
            await Until(() => activeA.Count == partitions.Length);
            var original = activeA.ToDictionary();
            runB = runnerB.RunAsync([.. partitions.Reverse()], (route, ct) => Hold(route, activeB, null, ct),
                options with { WorkerId = workerB }, cancelB.Token);
            var expectedB = partitions.Where(route => Owner(route, "a", workerB) == workerB)
                .ToHashSet(StringComparer.Ordinal);
            await Until(() => expectedB.SetEquals(activeB.Keys) && activeA.Count + activeB.Count == partitions.Length);
            foreach (var route in activeA.Keys)
            {
                Assert.Equal(original[route], activeA[route]);
                Assert.Equal(1, startsA[route]);
            }

            Assert.Empty(activeA.Keys.Intersect(activeB.Keys));
            if (crash)
            {
                await second.DisposeAsync();
            }
            else
            {
                cancelB.Cancel();
            }

            await Until(() => activeA.Count == partitions.Length);
            Assert.All(expectedB, route => Assert.Equal(2, startsA[route]));
            Assert.All(partitions.Except(expectedB), route => Assert.Equal(1, startsA[route]));
        }
        finally
        {
            cancelA.Cancel();
            cancelB.Cancel();
            await runA.WaitAsync(TimeSpan.FromSeconds(10));
            if (runB is not null)
            {
                await runB.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await second.DisposeAsync();
        }

        Assert.Empty(activeA);
        Assert.Empty(activeB);
    }

    [Fact]
    public async Task UnrenewedMembershipAndPartitionExpireBeforeSurvivorTakesOver()
    {
        await using var stalled = await ConsumerBroker.ConnectAsync();
        await using var survivor = await ConsumerBroker.ConnectAsync();
        var realm = "expiry-" + Guid.NewGuid().ToString("N");
        var partition = $"lease://{realm}/parts/one";
        var ghost = Enumerable.Range(0, 100).Select(i => "ghost" + i).First(id => Owner(partition, "a", id) == id);
        // Unmanaged leases intentionally receive no renewals. Keep the connection alive to
        // distinguish TTL expiry from a broker's session-disconnect cleanup.
        await using var member = await stalled.Lease.AcquireAsync($"lease://{realm}/members/{ghost}", 2);
        await using var lease = await stalled.Lease.AcquireAsync(partition, 2);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var runner = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(survivor.Lease),
            new FitzFleetMembership(survivor.Lease));
        var run = runner.RunAsync([partition], (route, ct) =>
        {
            _ = acquired.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, new FleetRunOptions
        {
            MembershipSelector = $"lease://{realm}/members/*",
            WorkerId = "a",
            LeaseTtl = TimeSpan.FromSeconds(2),
            ReconciliationInterval = TimeSpan.FromMilliseconds(100)
        }, cancellation.Token);
        try
        {
            await Task.Delay(300);
            Assert.False(acquired.Task.IsCompleted);
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }
    }

    static async Task Hold(string route, ConcurrentDictionary<string, bool> active,
        ConcurrentDictionary<string, int>? starts, CancellationToken ct)
    {
        Assert.True(active.TryAdd(route, true));
        _ = starts?.AddOrUpdate(route, 1, (_, count) => count + 1);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            _ = active.TryRemove(route, out _);
        }
    }

    static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate())
            await Task.Delay(20, deadline.Token);
    }

    static string Owner(string route, params string[] workers) =>
        workers.MaxBy(worker => Score(route, worker), StringComparer.Ordinal)!;

    static string Score(string route, string worker)
    {
        using var bytes = new MemoryStream();
        foreach (var value in new[] { route, worker })
        {
            var utf8 = Encoding.UTF8.GetBytes(value);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, utf8.Length);
            bytes.Write(length);
            bytes.Write(utf8);
        }

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
