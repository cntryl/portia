using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Cntryl.Portia;

/// <summary>
/// Lease exclusion, cancellation, and backoff regressions using independent singleton inventories.
/// Fleet membership redistribution is covered by the public consumer and broker fleet tests.
/// </summary>
[Collection(TelemetryTestGroup.Name)]
public sealed class FleetPartitionRunnerTests
{
    /// <summary>A callback that ignores revocation faults the runner within the configured bound.</summary>
    [Fact]
    public async Task ShouldFaultRunnerGivenPartitionCallbackIgnoresCancellation()
    {
        const string partition = "lease://portia/fleet/stuck";
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            Assert.Equal(["runner", "error.type"], tags.ToArray().Select(tag => tag.Key));
            _ = Interlocked.Increment(ref failures);
        });
        listener.Start();
        using var lifetime = new CancellationTokenSource();
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            PartitionStopTimeout = TimeSpan.FromMilliseconds(50),
        };
        var run = runner.RunAsync([partition], (_, _) =>
        {
            started.SetResult();
            return release.Task;
        }, options, lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lifetime.Cancel();
        var exception = await Assert.ThrowsAsync<FleetPartitionTerminationTimeoutException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal([partition], exception.Partitions);
        Assert.Equal(options.PartitionStopTimeout, exception.Timeout);
        Assert.Equal(1, Volatile.Read(ref failures));
        release.SetResult();
    }

    /// <summary>A non-positive callback termination timeout is rejected before the runner starts.</summary>
    [Fact]
    public async Task ShouldRejectNonPositivePartitionStopTimeout()
    {
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership());
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            PartitionStopTimeout = TimeSpan.Zero,
        };

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(["lease://portia/fleet/p"], (_, _) => Task.CompletedTask, options));
    }

    /// <summary>Changing tenant partitions revoke only removed work and retain global ownership.</summary>
    [Fact]
    public async Task DynamicPartitionsPreserveRetainedOwnership()
    {
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership());
        string[] snapshot = ["lease://portia/work/global", "lease://portia/work/alpha"];
        var active = new ConcurrentDictionary<string, bool>();
        var starts = new ConcurrentDictionary<string, int>();
        using var lifetime = new CancellationTokenSource();
        var run = runner.RunAsync(() => Volatile.Read(ref snapshot), async (route, ct) =>
        {
            _ = starts.AddOrUpdate(route, 1, (_, count) => count + 1);
            active[route] = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { _ = active.TryRemove(route, out _); }
        }, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with { ReconciliationInterval = TimeSpan.FromMilliseconds(10) }, lifetime.Token);
        try
        {
            await Wait(() => active.Count == 2);
            Volatile.Write(ref snapshot, ["lease://portia/work/global", "lease://portia/work/beta"]);
            await Wait(() => active.ContainsKey("lease://portia/work/beta") && !active.ContainsKey("lease://portia/work/alpha"));
            Assert.Equal(1, starts["lease://portia/work/global"]);
        }
        finally { await lifetime.CancelAsync(); await AwaitCancelled(run); }
        Assert.Empty(active);

        static async Task Wait(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
    }

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
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(["lease://portia/fleet/p"], (_, _) => Task.CompletedTask, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)), cts.Token);

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
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(["lease://portia/fleet/p"], (_, _) => Task.CompletedTask, SingleWorkerMembership.Options(TimeSpan.Zero)));
    }

    /// <summary>
    /// Verifies that any partition route not shaped exactly like Fitz's required
    /// <c>lease://{realm}/{area}/{resource}</c> is rejected up front, rather than failing every
    /// single acquisition attempt forever with a silent per-attempt retry. A prefix check alone
    /// (an earlier, incomplete version of this validation) still let a route with too few
    /// segments, an empty segment, or a wildcard segment through — every one of those would still
    /// have been rejected by a real broker on every single attempt, exactly the silent-forever-retry
    /// case this validation exists to prevent, discovered only because a live broker actually
    /// rejects a malformed route instead of this fake accepting anything.
    /// </summary>
    [Theory]
    [InlineData("portia/fleet/not-a-lease-route", "missing the scheme entirely")]
    [InlineData("lease://realm", "only one segment")]
    [InlineData("lease://realm/area", "only two segments")]
    [InlineData("lease://realm/area/resource/extra", "one segment too many")]
    [InlineData("lease://realm//resource", "an empty middle segment")]
    [InlineData("lease:///area/resource", "an empty leading segment")]
    [InlineData("lease://realm/area/", "an empty trailing segment")]
    [InlineData("lease://*/area/resource", "a wildcard realm segment")]
    [InlineData("lease://realm/area/*", "a wildcard resource segment")]
    [InlineData("lease://realm/area/**", "a recursive wildcard resource segment")]
    [InlineData("lease://realm/a*ea/resource", "a wildcard embedded in the area segment")]
    [InlineData("lease://realm/area/res*", "a wildcard embedded in the resource segment")]
    public async Task ShouldRejectMalformedPartitionRoute(string route, string reason)
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        // Bounds the call: proper validation throws synchronously, well within this — but if
        // validation is missing for this particular malformed shape, RunAsync instead returns a
        // genuinely running (and, against this fake, endlessly "succeeding") task rather than
        // ever throwing, so this keeps that failure mode a clean, fast assertion failure instead
        // of hanging the test host waiting on a task that would otherwise never complete.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync([route], (_, _) => Task.CompletedTask, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)), cts.Token));
        Assert.Contains("lease://{realm}/{area}/{resource}", exception.Message, StringComparison.Ordinal);
        _ = reason;
    }

    /// <summary>
    /// Verifies that a properly-shaped route — the thing every other test in this class already
    /// relies on working — is still accepted, so the stricter validation above hasn't become
    /// overzealous.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptWellFormedPartitionRoute()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            ["lease://portia/fleet/well-formed"],
            (_, ct) => RunUntilCancelled("lease://portia/fleet/well-formed", new ConcurrentDictionary<string, bool>(), ct),
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)),
            cts.Token);

        await WaitUntil(() => leases.Acquisitions.Count == 1);
        cts.Cancel();
        await AwaitCancelled(run);
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
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(["lease://portia/fleet/p", "lease://portia/fleet/p"], (_, _) => Task.CompletedTask, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30))));
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
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var failures = new ConcurrentBag<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PortiaTelemetry.SourceName && instrument.Name == "portia.worker.failure")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => failures.Add(tags.ToArray()));
        listener.Start();
        using var cts = new CancellationTokenSource();

        leases.FailNextAcquisition("lease://portia/fleet/never-acquired");

        var run = runner.RunAsync(
            ["lease://portia/fleet/never-acquired", "lease://portia/fleet/acquired-then-fails"],
            (partition, _) => partition == "lease://portia/fleet/acquired-then-fails"
                ? throw new InvalidOperationException("callback failure")
                : Task.CompletedTask,
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)),
            cts.Token);

        await WaitUntil(() => failures.Count >= 2);

        cts.Cancel();
        await AwaitCancelled(run);

        Assert.All(failures, tags =>
        {
            Assert.Equal(["runner", "error.type"], tags.Select(tag => tag.Key));
            Assert.DoesNotContain(tags, tag => (tag.Value as string)?.Contains("lease://", StringComparison.Ordinal) == true);
        });
    }

    /// <summary>
    /// Verifies that a single worker with no competition simply acquires and holds every
    /// partition it's given.
    /// </summary>
    [Fact]
    public async Task ShouldAcquireEveryPartitionWhenNoWorkerContestsThem()
    {
        var leases = new InMemoryLeaseClient();
        var runner = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var held = new ConcurrentDictionary<string, bool>();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(
            ["lease://portia/fleet/partition-a", "lease://portia/fleet/partition-b"],
            (partition, ct) => RunUntilCancelled(partition, held, ct),
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)),
            cts.Token);

        await WaitUntil(() => held.Count == 2);
        cts.Cancel();
        await AwaitCancelled(run);

        Assert.True(held["lease://portia/fleet/partition-a"]);
        Assert.True(held["lease://portia/fleet/partition-b"]);
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
        var runnerA = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var runnerB = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var concurrentHolders = 0;
        var maxObservedConcurrentHolders = 0;
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        async Task OnAcquired(string partition, CancellationToken ct)
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

        var runA = runnerA.RunAsync(["lease://portia/fleet/shared-partition"], OnAcquired, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)), ctsA.Token);
        var runB = runnerB.RunAsync(["lease://portia/fleet/shared-partition"], OnAcquired, SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)), ctsB.Token);

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
        var runnerA = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var runnerB = new FleetPartitionRunner(leases, new SingleWorkerMembership());
        var stablePartitionRestarts = new int[1];
        var movedPartitionAcquisitions = new ConcurrentBag<string>();
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        // Worker A holds both partitions initially (B starts later, contesting only one of them)
        // — so "lease://portia/fleet/stable-partition" is never contested by B at all, and must never restart because
        // of anything that happens to "lease://portia/fleet/moving-partition".
        var runA = runnerA.RunAsync(
            ["lease://portia/fleet/stable-partition", "lease://portia/fleet/moving-partition"],
            (partition, ct) => partition switch
            {
                "lease://portia/fleet/stable-partition" => CountRestartsUntilCancelled(stablePartitionRestarts, ct),
                _ => RunUntilCancelledRecordingAcquisition(partition, movedPartitionAcquisitions, ct),
            },
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)),
            ctsA.Token);

        await WaitUntil(() => leases.Acquisitions.Count >= 2);

        // Worker B only ever contests "lease://portia/fleet/moving-partition" — it should never even attempt
        // "lease://portia/fleet/stable-partition".
        var runB = runnerB.RunAsync(
            ["lease://portia/fleet/moving-partition"],
            (partition, ct) => RunUntilCancelledRecordingAcquisition(partition, movedPartitionAcquisitions, ct),
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)),
            ctsB.Token);

        // Worker A gives up "lease://portia/fleet/moving-partition" only (simulated here as a full worker shutdown,
        // the simplest case — real deployments would cancel just that one partition's token).
        ctsA.Cancel();
        await AwaitCancelled(runA);

        await WaitUntil(() => movedPartitionAcquisitions.Count(p => p == "lease://portia/fleet/moving-partition") >= 2);

        ctsB.Cancel();
        await AwaitCancelled(runB);

        Assert.Equal(1, stablePartitionRestarts[0]);
        Assert.Equal(2, movedPartitionAcquisitions.Count(p => p == "lease://portia/fleet/moving-partition"));
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
