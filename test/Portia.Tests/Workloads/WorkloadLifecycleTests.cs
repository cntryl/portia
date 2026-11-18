namespace Cntryl.Portia;

/// <summary>Workload failures and source identity survive optional notification transport failures.</summary>
public sealed class WorkloadLifecycleTests
{
    /// <summary>Subscription cleanup cannot replace the exhausted workload failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetainTerminalFailureGivenSubscriptionDisposal(bool cleanupFails)
    {
        // Arrange
        await using var provider = WorkloadLifecycleTestHost.Create();
        var original = new IOException("workload pass failed");
        var descriptor = new WorkloadLifecycleDescriptor(_ => ValueTask.FromException<ProjectionPassResult>(original));
        var notifier = new WorkloadLifecycleNotifier(cleanupFails);
        using var worker = WorkloadLifecycleTestHost.Worker(provider, descriptor, notifier);
        await worker.StartAsync(default);

        // Act
        var failure = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        await worker.StopAsync(default);

        // Assert
        var terminal = Assert.IsType<WorkloadFailureException>(failure);
        Assert.Same(original, terminal.InnerException);
        if (cleanupFails)
            Assert.Same(notifier.CleanupFailure, terminal.Data["Portia.CleanupException"]);
    }

    /// <summary>Source identity stays stable with polling, healthy notifications, or a failed subscription.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldRejectChangedPatternGivenPollingOrNotificationMode(int notificationMode)
    {
        // Arrange
        await using var provider = WorkloadLifecycleTestHost.Create();
        var patterns = new List<string>();
        var reads = 0;
        var current = "first";
        var descriptor = new WorkloadLifecycleDescriptor(_ =>
        {
            patterns.Add(current);
            return current == "first"
                ? ValueTask.FromResult(new ProjectionPassResult(default, true))
                : ValueTask.FromException<ProjectionPassResult>(new IOException("processed second pattern"));
        }, () => EventStreamPattern.ForPattern("audit", current = ++reads <= 2 ? "first" : "second"));
        using var worker = WorkloadLifecycleTestHost.Worker(provider, descriptor,
            notificationMode == 0
                ? null
                : new WorkloadLifecycleNotifier(false, throwOnSubscribe: notificationMode == 2));
        await worker.StartAsync(default);

        // Act
        var failure = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
        await worker.StopAsync(default);

        // Assert
        var terminal = Assert.IsType<WorkloadFailureException>(failure);
        Assert.Contains("changed its event-stream pattern", terminal.InnerException!.Message, StringComparison.Ordinal);
        Assert.Contains("first", patterns);
        Assert.DoesNotContain("second", patterns);
    }

    /// <summary>A failed notification wait and failed disposal leave healthy work running through polling.</summary>
    [Fact]
    public async Task ShouldContinueProcessingGivenNotificationWaitAndCleanupFail()
    {
        // Arrange
        await using var provider = WorkloadLifecycleTestHost.Create();
        var processedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var descriptor = new WorkloadLifecycleDescriptor(_ =>
        {
            if (Interlocked.Increment(ref passes) == 2)
                processedAgain.TrySetResult();
            return ValueTask.FromResult(default(ProjectionPassResult));
        });
        var notifier = new RecoveringNotifier();
        using var worker = WorkloadLifecycleTestHost.Worker(provider, descriptor, notifier);
        await worker.StartAsync(default);
        Exception? processingError;
        bool endedPrematurely;

        // Act
        try
        {
            processingError = await Record.ExceptionAsync(() => processedAgain.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            endedPrematurely = worker.ExecuteTask!.IsCompleted;
        }
        finally
        {
            await worker.StopAsync(default);
        }

        // Assert
        Assert.Null(processingError);
        Assert.False(endedPrematurely);
        Assert.Equal(1, notifier.FailedDisposals);
        Assert.Equal(2, notifier.Subscriptions);
    }

    sealed class RecoveringNotifier : IDomainEventNotifier
    {
        internal int Subscriptions { get; private set; }
        internal int FailedDisposals { get; private set; }

        public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern,
            CancellationToken ct = default)
            => ValueTask.FromResult<IDomainEventSubscription>(++Subscriptions == 1
                ? new FailedSubscription(this)
                : new HealthySubscription());

        sealed class FailedSubscription(RecoveringNotifier owner) : IDomainEventSubscription
        {
            public ValueTask WaitAsync(CancellationToken ct = default)
                => ValueTask.FromException(new IOException("notification wait failed"));

            public ValueTask DisposeAsync()
            {
                owner.FailedDisposals++;
                return ValueTask.FromException(new IOException("notification disposal failed"));
            }
        }

        sealed class HealthySubscription : IDomainEventSubscription
        {
            public ValueTask WaitAsync(CancellationToken ct = default) => new(Task.Delay(Timeout.InfiniteTimeSpan, ct));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
