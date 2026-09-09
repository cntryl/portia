namespace Cntryl.Portia;

/// <summary>
/// Verifies renewable partition leases and disconnect handoff against Compose, with singleton
/// membership fixtures. Public FleetBrokerTests separately verify real membership scale-out and TTL expiry.
/// </summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
public sealed class FitzBrokerFleetIntegrationTests(FitzBrokerFixture broker)
{
    readonly FitzBrokerFixture _broker = broker;

    /// <summary>
    /// Verifies that a lease held by <see cref="FleetPartitionRunner" /> survives well past its
    /// own TTL, as long as the callback holding it keeps running — proving Fitz really does
    /// renew it automatically, not just that the API shape suggests it should.
    /// </summary>
    [Fact]
    public async Task ShouldRenewLeaseAutomaticallyWhileCallbackOutlivesTtl()
    {
        await using var client = await _broker.CreateClientAsync();
        var runner = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(client.Lease), new SingleWorkerMembership());
        var partition = $"lease://portia-integration/fleet/renewal-{Uuid.CreateVersion4()}";
        using var cts = new CancellationTokenSource();
        var iterationsCompleted = 0;
        var everCancelledBeforeWeAskedIt = false;

        var run = runner.RunAsync(
            [partition],
            async (_, ct) =>
            {
                // TTL is 2s; this callback runs for 6s — more than 2 renewal cycles. If Fitz
                // were not actually renewing the lease behind the scenes, WithLeaseAsync would
                // have long since torn this callback's token down well before this completes.
                for (var i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    iterationsCompleted++;
                }

                everCancelledBeforeWeAskedIt = ct.IsCancellationRequested;
            },
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(2)),
            cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(7));
        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Expected — this test's own cancellation, not the lease being lost.
        }

        // Confirms the callback actually ran the whole way through — not just that the "was it
        // cancelled early" flag stayed at its unset default because the callback never ran at
        // all (e.g. the lease was never acquired in the first place, a real false-positive this
        // test hit once already with a malformed route).
        Assert.Equal(6, iterationsCompleted);
        Assert.False(everCancelledBeforeWeAskedIt);
    }

    /// <summary>
    /// Verifies lease handoff: a worker holding a partition's lease that
    /// disappears without releasing it gracefully (its connection is torn down, not a clean
    /// shutdown) still frees that partition for another, already-waiting worker once the lease's
    /// TTL lapses — real crash recovery, not just a cooperative handoff.
    /// </summary>
    [Fact]
    public async Task ShouldGrantPartitionToWaitingWorkerWhenHolderDisconnectsWithoutReleasing()
    {
        var holderClient = await _broker.CreateClientAsync();
        await using var waiterClient = await _broker.CreateClientAsync();
        var partition = $"lease://portia-integration/fleet/crash-recovery-{Uuid.CreateVersion4()}";
        var holderAcquired = new TaskCompletionSource();
        var waiterAcquired = new TaskCompletionSource();

        var holderRunner = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(holderClient.Lease), new SingleWorkerMembership());
        using var holderCts = new CancellationTokenSource();
        var holderRun = holderRunner.RunAsync(
            [partition],
            (route, ct) =>
            {
                _ = route;
                _ = holderAcquired.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(2)),
            holderCts.Token);

        await holderAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var waiterRunner = new FleetPartitionRunner(new FitzPartitionLeaseCompetitor(waiterClient.Lease), new SingleWorkerMembership());
        using var waiterCts = new CancellationTokenSource();
        var waiterRun = waiterRunner.RunAsync(
            [partition],
            (route, ct) =>
            {
                _ = route;
                _ = waiterAcquired.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            SingleWorkerMembership.Options(TimeSpan.FromSeconds(2)),
            waiterCts.Token);

        // Simulate a crash: tear down the holder's connection without ever cancelling its own
        // token or letting FleetPartitionRunner release the lease cooperatively.
        await holderClient.DisposeAsync();

        // TTL is 2s; give it real margin past that for the broker to expire the lease and for
        // the waiter's blocked WithLeaseAsync call to actually win it.
        await waiterAcquired.Task.WaitAsync(TimeSpan.FromSeconds(15));

        waiterCts.Cancel();
        try
        {
            await waiterRun;
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        holderCts.Cancel();
        try
        {
            await holderRun;
        }
        catch (Exception)
        {
            // The holder's own connection is already gone; whatever this faults with isn't the
            // point of this test — only that the waiter really did take over is.
        }

        Assert.True(waiterAcquired.Task.IsCompletedSuccessfully);
    }
}
