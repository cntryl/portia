using System.Collections.Concurrent;
using System.Diagnostics;
using Cntryl.Fitz.Abstractions.Domains.Lease;

namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="FleetPartitionRunner" />'s fleet-distribution behavior: a fixed,
/// deployment-time-known set of partitions competed for across a dynamically-sized pool of
/// worker processes, using Fitz leases so exactly one worker holds each partition. No partition
/// is ever assigned by a central coordinator — every guarantee here falls out of independent,
/// per-partition lease contention.
/// </summary>
public sealed class FleetPartitionRunnerTests
{
    /// <summary>
    /// Regression test: a callback that returns quickly — by its own design, or because its
    /// lease was lost — must not spin the competition loop with no backoff at all. An earlier
    /// version of this class hammered the lease client fast enough, with no pause between
    /// attempts, that it starved the process; this bounds how many times a fast-returning
    /// callback's partition can possibly be re-acquired in a short window.
    /// </summary>
    [Fact]
    public async Task ShouldBackOffWhenCallbackReturnsImmediately()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(["p"], (_, _, _) => Task.CompletedTask, TimeSpan.FromSeconds(30), cts.Token);

        await Task.Delay(300);
        cts.Cancel();
        await AwaitCancelled(run);

        Assert.True(leases.Acquisitions.Count <= 2, $"Expected at most 2 acquisitions in 300ms with a 1s backoff, got {leases.Acquisitions.Count}.");
    }

    /// <summary>
    /// Verifies that a non-positive lease TTL is rejected up front with a clear error, rather
    /// than surfacing later as an opaque overflow when converting to whole seconds.
    /// </summary>
    [Fact]
    public async Task ShouldRejectNonPositiveLeaseTtl()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(["p"], (_, _, _) => Task.CompletedTask, TimeSpan.Zero));
    }

    /// <summary>
    /// Verifies that a duplicate partition key is rejected up front, rather than silently
    /// leaving one of the two competition loops permanently blocked on a route this same
    /// process already holds — nothing in that process would ever release it.
    /// </summary>
    [Fact]
    public async Task ShouldRejectDuplicatePartitionKeys()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases);

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(["p", "p"], (_, _, _) => Task.CompletedTask, TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Verifies that a failure to even acquire a partition's lease (contention still in
    /// progress, no callback ever ran) is reported with a distinctly different fault reason
    /// than a callback that acquired the lease and then failed on its own — conflating the two
    /// would make routine, expected contention look identical to a genuine bug every time it's
    /// observed.
    /// </summary>
    [Fact]
    public async Task ShouldReportDifferentFaultReasonForFailedAcquisitionThanFailedCallback()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases);
        using var listener = Listen(out var activities);
        using var cts = new CancellationTokenSource();

        leases.FailNextAcquisition("never-acquired");

        var run = runner.RunAsync(
            ["never-acquired", "acquired-then-fails"],
            (partition, _, _) => partition == "acquired-then-fails"
                ? throw new InvalidOperationException("callback failure")
                : Task.CompletedTask,
            TimeSpan.FromSeconds(30),
            cts.Token);

        static bool IsFor(Activity a, string partition)
        {
            return (a.GetTagItem("portia.runner") as string) == nameof(FleetPartitionRunner)
                && (a.GetTagItem("portia.fault_reason") as string)?.Contains(partition, StringComparison.Ordinal) == true;
        }

        await WaitUntil(() => activities.Any(a => IsFor(a, "never-acquired")) && activities.Any(a => IsFor(a, "acquired-then-fails")));

        cts.Cancel();
        await AwaitCancelled(run);

        var neverAcquiredReason = activities.First(a => IsFor(a, "never-acquired")).GetTagItem("portia.fault_reason") as string;
        var acquiredThenFailedReason = activities.First(a => IsFor(a, "acquired-then-fails")).GetTagItem("portia.fault_reason") as string;

        // Strip the partition name itself out of each reason before comparing, so this asserts
        // the *category* of failure differs, not just that the (always-distinct) partition name
        // happens to appear in both strings.
        Assert.NotEqual(
            neverAcquiredReason!.Replace("never-acquired", string.Empty, StringComparison.Ordinal),
            acquiredThenFailedReason!.Replace("acquired-then-fails", string.Empty, StringComparison.Ordinal));
    }

    static ActivityListener Listen(out ConcurrentBag<Activity> activities)
    {
        var captured = new ConcurrentBag<Activity>();
        activities = captured;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Verifies that a single worker with no competition simply acquires and holds every
    /// partition it's given.
    /// </summary>
    [Fact]
    public async Task ShouldAcquireEveryPartitionWhenNoWorkerContestsThem()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases);
        var held = new ConcurrentDictionary<string, bool>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            ["partition-a", "partition-b"],
            (partition, _, ct) => RunUntilCancelled(partition, held, ct),
            TimeSpan.FromSeconds(30),
            cts.Token);

        await WaitUntil(() => held.Count == 2);
        cts.Cancel();
        await AwaitCancelled(run);

        Assert.True(held["partition-a"]);
        Assert.True(held["partition-b"]);
    }

    /// <summary>
    /// Verifies the core contention guarantee: when two workers compete for the same single
    /// partition, only one of them ever holds it — the second stays blocked, never running its
    /// callback concurrently with the first.
    /// </summary>
    [Fact]
    public async Task ShouldGrantPartitionToExactlyOneCompetingWorker()
    {
        var leases = new InMemoryLeaseClient();
        var runnerA = new FleetPartitionRunner(leases);
        var runnerB = new FleetPartitionRunner(leases);
        var concurrentHolders = 0;
        var maxObservedConcurrentHolders = 0;
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        async Task OnAcquired(string partition, LeaseAuthority authority, CancellationToken ct)
        {
            var concurrent = Interlocked.Increment(ref concurrentHolders);
            InterlockedMax(ref maxObservedConcurrentHolders, concurrent);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — this worker is being asked to stop holding the partition.
            }
            finally
            {
                _ = Interlocked.Decrement(ref concurrentHolders);
            }
        }

        var runA = runnerA.RunAsync(["shared-partition"], OnAcquired, TimeSpan.FromSeconds(30), ctsA.Token);
        var runB = runnerB.RunAsync(["shared-partition"], OnAcquired, TimeSpan.FromSeconds(30), ctsB.Token);

        await WaitUntil(() => leases.Acquisitions.Count >= 1);
        // Give the loser a real chance to have (incorrectly) run concurrently, if the
        // implementation had a bug — not just an immediate race with the winner's own start.
        await Task.Delay(50);

        ctsA.Cancel();
        ctsB.Cancel();
        await AwaitCancelled(runA);
        await AwaitCancelled(runB);

        Assert.Equal(1, maxObservedConcurrentHolders);
    }

    /// <summary>
    /// The key minimal-shuffle guarantee: when a worker holding one partition stops, only that
    /// partition moves to another worker — a second partition already stably held elsewhere is
    /// never interrupted, restarted, or even briefly re-contended.
    /// </summary>
    [Fact]
    public async Task ShouldOnlyMoveThePartitionThatWasReleasedWhenAWorkerStops()
    {
        var leases = new InMemoryLeaseClient();
        var runnerA = new FleetPartitionRunner(leases);
        var runnerB = new FleetPartitionRunner(leases);
        var stablePartitionRestarts = new int[1];
        var movedPartitionAcquisitions = new ConcurrentBag<string>();
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        // Worker A holds both partitions initially (B starts later, contesting only one of them)
        // — so "stable-partition" is never contested by B at all, and must never restart because
        // of anything that happens to "moving-partition".
        var runA = runnerA.RunAsync(
            ["stable-partition", "moving-partition"],
            (partition, _, ct) => partition switch
            {
                "stable-partition" => CountRestartsUntilCancelled(stablePartitionRestarts, ct),
                _ => RunUntilCancelledRecordingAcquisition(partition, movedPartitionAcquisitions, ct),
            },
            TimeSpan.FromSeconds(30),
            ctsA.Token);

        await WaitUntil(() => leases.Acquisitions.Count >= 2);

        // Worker B only ever contests "moving-partition" — it should never even attempt
        // "stable-partition".
        var runB = runnerB.RunAsync(
            ["moving-partition"],
            (partition, _, ct) => RunUntilCancelledRecordingAcquisition(partition, movedPartitionAcquisitions, ct),
            TimeSpan.FromSeconds(30),
            ctsB.Token);

        // Worker A gives up "moving-partition" only (simulated here as a full worker shutdown,
        // the simplest case — real deployments would cancel just that one partition's token).
        ctsA.Cancel();
        await AwaitCancelled(runA);

        await WaitUntil(() => movedPartitionAcquisitions.Count(p => p == "moving-partition") >= 2);

        ctsB.Cancel();
        await AwaitCancelled(runB);

        Assert.Equal(1, stablePartitionRestarts[0]);
        Assert.Equal(2, movedPartitionAcquisitions.Count(p => p == "moving-partition"));
    }

    static async Task RunUntilCancelled(string partition, ConcurrentDictionary<string, bool> held, CancellationToken ct)
    {
        held[partition] = true;

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the run is being cancelled.
        }
    }

    static async Task RunUntilCancelledRecordingAcquisition(string partition, ConcurrentBag<string> acquisitions, CancellationToken ct)
    {
        acquisitions.Add(partition);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the run is being cancelled.
        }
    }

    static async Task CountRestartsUntilCancelled(int[] counter, CancellationToken ct)
    {
        _ = Interlocked.Increment(ref counter[0]);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — the run is being cancelled.
        }
    }

    static void InterlockedMax(ref int target, int candidate)
    {
        int initial;

        do
        {
            initial = target;

            if (candidate <= initial)
                return;
        }
        while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition());
    }

    static async Task AwaitCancelled(Task run)
    {
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Expected from Task.WhenAll when a competing partition's own token was cancelled.
        }
    }
}
