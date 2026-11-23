namespace Cntryl.Portia;

/// <summary>Revocation observes terminal faults and cancels dependent siblings together.</summary>
public sealed class FleetLifecycleTests
{
    /// <summary>The stop deadline also covers synchronous handlers registered on the partition token.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldBoundBlockingCancellationCallbackGivenRevocationOrShutdown(bool shutdown)
    {
        // Arrange
        const string partition = "lease://test/work/blocked";
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership());
        using var lifetime = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] snapshot = [partition];
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(10),
            PartitionStopTimeout = TimeSpan.FromMilliseconds(50)
        };
        var run = runner.RunAsync(() => Volatile.Read(ref snapshot), (_, token) =>
        {
            // Keep the registration alive even after the workload task has finished.
            token.Register(() =>
            {
                started.TrySetResult();
                release.Wait();
                finished.TrySetResult();
            });
            ready.TrySetResult();
            return started.Task;
        }, options, lifetime.Token);
        // Wait until the first partition has installed its callback before requesting a stop.
        Exception? error;
        var stopping = Task.CompletedTask;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        try
        {
            if (shutdown)
                stopping = lifetime.CancelAsync();
            else
                Volatile.Write(ref snapshot, []);

            error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await stopping;
            await lifetime.CancelAsync();
            _ = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }

        // Assert
        Assert.Equal([partition], Assert.IsType<FleetPartitionTerminationTimeoutException>(error).Partitions);
    }

    /// <summary>A timeout during final cleanup preserves the earlier revocation timeout and its route.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveRevokedTimeoutGivenAnotherFailureDuringShutdown(bool workloadFails)
    {
        // Arrange
        const string revoked = "lease://test/work/revoked";
        const string retained = "lease://test/work/retained";
        var runner = new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var failure = new WorkloadFailureException(new WorkloadIdentity("retained"), 1, new IOException("terminal"));
        string[] snapshot = [retained, revoked];
        var options = SingleWorkerMembership.Options(TimeSpan.FromSeconds(30)) with
        {
            ReconciliationInterval = TimeSpan.FromMilliseconds(10),
            PartitionStopTimeout = TimeSpan.FromMilliseconds(50)
        };
        using var lifetime = new CancellationTokenSource();
        var run = runner.RunAsync(() => Volatile.Read(ref snapshot), async (route, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
                ready.TrySetResult();

            if (workloadFails && route == retained)
            {
                var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => stop.TrySetResult());
                await stop.Task;
                throw failure;
            }

            await release.Task;
        }, options, lifetime.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Exception? error;

        // Act
        try
        {
            Volatile.Write(ref snapshot, [retained]);
            error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.TrySetResult();
            await lifetime.CancelAsync();
        }

        // Assert
        if (workloadFails)
        {
            Assert.Same(failure, error);
            var timeout = Assert.IsType<FleetPartitionTerminationTimeoutException>(failure.Data["Portia.CleanupException"]);
            Assert.Equal([revoked], timeout.Partitions);
        }
        else
        {
            var primary = Assert.IsType<FleetPartitionTerminationTimeoutException>(error);
            Assert.Equal([revoked], primary.Partitions);
            var cleanup = Assert.IsType<FleetPartitionTerminationTimeoutException>(primary.Data["Portia.CleanupException"]);
            Assert.Equal([retained], cleanup.Partitions);
        }
    }

    /// <summary>Revocation retains a terminal fault, including when another revoked callback times out.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPropagateTerminalFailureGivenPartitionRevokedAfterFault(bool stuckSibling)
    {
        // Arrange
        const string route = "lease://test/work/failed";
        const string stuck = "lease://test/work/stuck";
        var interval = TimeSpan.FromHours(1);
        var timeout = TimeSpan.FromMinutes(10);
        var clock = new ManualTestClock();
        var runner =
            new FleetPartitionRunner(new InMemoryLeaseClient(), new SingleWorkerMembership(), timeProvider: clock);
        var terminal = new WorkloadFailureException(new WorkloadIdentity("test"), 1,
            new IOException("permanent workload failure"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Complete the failure through the lease/competition await chain inline before advancing
        // the clock. Otherwise the test can expire both callbacks before the failed one resumes.
        var completion = new TaskCompletionSource();
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] snapshot = stuckSibling ? [route, stuck] : [route];
        using var lifetime = new CancellationTokenSource();
        var options = new FleetRunOptions
        {
            MembershipSelector = "lease://test/members/*",
            ReconciliationInterval = interval,
            PartitionStopTimeout = timeout
        };
        var starts = 0;
        var run = runner.RunAsync(() => Volatile.Read(ref snapshot), (partition, _) =>
        {
            if (Interlocked.Increment(ref starts) == (stuckSibling ? 2 : 1))
                started.TrySetResult();

            return partition == stuck ? releaseSibling.Task : completion.Task;
        }, options, lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var openingDelay = await clock.WaitForDelayAsync(lifetime.Token);
        Exception? failure = null;
        Task? nextReconciliation = null;

        async Task WaitForDelayAsync(TimeSpan expected)
        {
            while (await clock.WaitForDelayAsync(lifetime.Token) != expected)
            {
            }
        }

        // Act
        try
        {
            Volatile.Write(ref snapshot, []);
            completion.SetException(terminal);
            clock.Advance(interval);

            if (stuckSibling)
            {
                await WaitForDelayAsync(timeout).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                clock.Advance(timeout);
                failure = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            }
            else
            {
                nextReconciliation = WaitForDelayAsync(interval);
                await Task.WhenAny(run, nextReconciliation).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

                if (run.IsCompleted)
                    failure = await Record.ExceptionAsync(() => run);
            }
        }
        finally
        {
            releaseSibling.TrySetResult();
            await lifetime.CancelAsync();
            _ = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            if (nextReconciliation is not null)
                _ = await Record.ExceptionAsync(() => nextReconciliation);
        }

        // Assert
        Assert.Equal(interval, openingDelay);
        Assert.Same(terminal, failure);

        if (stuckSibling)
        {
            var cleanup =
                Assert.IsType<FleetPartitionTerminationTimeoutException>(terminal.Data["Portia.CleanupException"]);
            Assert.Equal([stuck], cleanup.Partitions);
        }
        else
            Assert.Null(terminal.Data["Portia.CleanupException"]);
    }

    /// <summary>Sibling cleanup receives every cancellation signal before any revoked callback is awaited.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCancelSiblingsTogetherGivenRevocationOrTerminalFailure(bool terminal)
    {
        // Arrange
        var clock = new ManualTestClock();
        var interval = TimeSpan.FromHours(1);
        var coordinator = new SingleProcessWorkloadCoordinator(interval, clock);
        var a = new WorkloadIdentity("a");
        var b = new WorkloadIdentity("b");
        var c = new WorkloadIdentity("c");
        var terminalFailure = new WorkloadFailureException(a, 1, new IOException("terminal"));
        WorkloadIdentity[] snapshot = terminal ? [a, b, c] : [b, c];
        var tokens = new Dictionary<WorkloadIdentity, CancellationToken>();
        var registrations = new List<CancellationTokenRegistration>();
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The next reconciliation must observe the completed fault, not race its queued continuation.
        var fault = new TaskCompletionSource();
        using var lifetime = new CancellationTokenSource();
        var run = coordinator.RunAsync(() => Volatile.Read(ref snapshot), async (id, ct) =>
        {
            tokens[id] = ct;

            if (id == a)
            {
                await fault.Task.ConfigureAwait(false);
                return;
            }

            // The test retains registrations so a resumed cancelled delay cannot dispose them
            // before CancelAsync invokes the application's callback.

            registrations.Add(ct.Register(() => (id == b ? firstCancelled : secondCancelled).TrySetResult()));

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }


            if (id == b)
                await secondCancelled.Task;
        }, lifetime.Token);
        var openingDelay = await clock.WaitForDelayAsync(lifetime.Token);
        var bothCancelled = false;
        Exception? failure = null;

        // Act
        try
        {
            if (terminal)
                fault.SetException(terminalFailure);
            else
                Volatile.Write(ref snapshot, []);

            clock.Advance(interval);
            await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            _ = await Record.ExceptionAsync(() => secondCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            bothCancelled = tokens[b].IsCancellationRequested && tokens[c].IsCancellationRequested;

            if (terminal && bothCancelled)
                failure = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            await lifetime.CancelAsync();
            _ = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

            foreach (var registration in registrations)
                registration.Dispose();
        }

        // Assert
        Assert.Equal(interval, openingDelay);
        Assert.True(bothCancelled,
            "Sibling c was still running while the coordinator awaited b's cooperative cleanup.");

        if (terminal)
            Assert.Same(terminalFailure, failure);
    }
}
