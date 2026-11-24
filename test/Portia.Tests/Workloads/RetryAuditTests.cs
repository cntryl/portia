using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Exercises the hosted retry boundary when pass-scope cleanup throws.</summary>
public sealed class RetryAuditTests
{
    /// <summary>Permanent failures stop immediately without operations and durably block with operations.</summary>
    /// <param name="reactor">Whether the hosted processor is a reactor instead of a projector.</param>
    /// <param name="operations">Whether durable operations are enabled.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldHonorPermanentFailureGivenHostedProcessorAndOperationsMode(
        bool reactor,
        bool operations
    )
    {
        // Arrange
        var original = Failure(transient: false);
        var descriptor = new AuditDescriptor(kind: reactor ? ProcessorKinds.Reactor : ProcessorKinds.Projector,
            run: (_, _) => ValueTask.FromException<ProjectionPassResult>(original));
        var journal = operations ? new MemoryOperationalStore() : null;
        await using var provider = Provider(new DisposalProbe { Fail = false }, journal);
        using var worker = Worker(provider, descriptor, attempts: 3);

        // Act
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var error = operations
            ? await WaitForBlockedAsync(worker, journal!, TestContext.Current.CancellationToken)
            : await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));

        if (operations)
            await worker.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, descriptor.Runs);

        if (operations)
        {
            Assert.Null(error);
            var state = await journal!.ReadAsync(Target, TestContext.Current.CancellationToken);
            Assert.NotNull(state!.Block);
            Assert.Equal(1, state.ConsecutiveFailures);
            var attempt = Assert.Single(state.Attempts);
            Assert.False(attempt.IsTransient);
            Assert.Equal("Validation", attempt.FailureKind);
            Assert.Equal(OperationalAttemptOutcomes.Failed, attempt.Outcome);
        }
        else
        {
            var terminal = Assert.IsType<WorkloadFailureException>(error);
            Assert.Equal(1, terminal.Attempts);
            Assert.Same(original, terminal.InnerException);
        }
    }

    /// <summary>A transient failure enters backoff and the next pass resumes processing.</summary>
    /// <param name="reactor">Whether the hosted processor is a reactor instead of a projector.</param>
    /// <param name="operations">Whether durable operations are enabled.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldRetryTransientFailureGivenHostedProcessorAndOperationsMode(
        bool reactor,
        bool operations
    )
    {
        // Arrange
        var original = Failure(transient: true);
        var descriptor = new AuditDescriptor(kind: reactor ? ProcessorKinds.Reactor : ProcessorKinds.Projector,
            run: (run, _) => run == 1
                ? ValueTask.FromException<ProjectionPassResult>(original)
                : ValueTask.FromResult(default(ProjectionPassResult)));
        var journal = operations ? new MemoryOperationalStore() : null;
        await using var provider = Provider(new DisposalProbe { Fail = false }, journal);
        using var worker = Worker(provider, descriptor, attempts: 3);
        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Act
        if (operations)
            await WaitAsync(async () =>
                    descriptor.Runs >= 2 &&
                    (await journal!.ReadAsync(Target, TestContext.Current.CancellationToken)) is
                    { ConsecutiveFailures: 0 },
                TestContext.Current.CancellationToken);
        else
            await WaitAsync(() => descriptor.Runs >= 2, TestContext.Current.CancellationToken);

        await worker.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(descriptor.Runs >= 2);
        Assert.False(worker.ExecuteTask!.IsFaulted);

        if (operations)
        {
            var state = await journal!.ReadAsync(Target, TestContext.Current.CancellationToken);
            Assert.NotNull(state);
            Assert.Null(state.Block);
            Assert.Equal(0, state.ConsecutiveFailures);
            Assert.Single(state.Attempts);
            Assert.Equal(OperationalAttemptOutcomes.Failed, state.Attempts[0].Outcome);
        }
    }

    /// <summary>Repeated transient failures consume exactly the configured retry budget.</summary>
    /// <param name="reactor">Whether the hosted processor is a reactor instead of a projector.</param>
    /// <param name="operations">Whether durable operations are enabled.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldExhaustRetryBudgetGivenHostedProcessorAndOperationsMode(
        bool reactor,
        bool operations
    )
    {
        // Arrange
        var original = Failure(transient: true);
        var descriptor = new AuditDescriptor(kind: reactor ? ProcessorKinds.Reactor : ProcessorKinds.Projector,
            run: (_, _) => ValueTask.FromException<ProjectionPassResult>(original));
        var journal = operations ? new MemoryOperationalStore() : null;
        await using var provider = Provider(new DisposalProbe { Fail = false }, journal);
        using var worker = Worker(provider, descriptor, attempts: 2);

        // Act
        await worker.StartAsync(TestContext.Current.CancellationToken);
        var error = operations
            ? await WaitForBlockedAsync(worker, journal!, TestContext.Current.CancellationToken)
            : await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));

        if (operations)
            await worker.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, descriptor.Runs);

        if (operations)
        {
            Assert.Null(error);
            var state = await journal!.ReadAsync(Target, TestContext.Current.CancellationToken);
            Assert.NotNull(state!.Block);
            Assert.Equal(2, state.ConsecutiveFailures);
            Assert.Equal(2, state.Attempts.Count);
        }
        else
        {
            var terminal = Assert.IsType<WorkloadFailureException>(error);
            Assert.Equal(2, terminal.Attempts);
            Assert.Same(original, terminal.InnerException);
        }
    }

    /// <summary>Requested host cancellation interrupts both processor kinds without becoming a failure.</summary>
    /// <param name="reactor">Whether the hosted processor is a reactor instead of a projector.</param>
    /// <param name="operations">Whether durable operations are enabled.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldHonorRequestedCancellationGivenHostedProcessorAndOperationsMode(
        bool reactor,
        bool operations
    )
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = new AuditDescriptor(kind: reactor ? ProcessorKinds.Reactor : ProcessorKinds.Projector,
            run: async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return default;
            });
        var journal = operations ? new MemoryOperationalStore() : null;
        await using var provider = Provider(new DisposalProbe { Fail = false }, journal);
        using var worker = Worker(provider, descriptor, attempts: 2);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // Act
        var error = await Record.ExceptionAsync(() => worker.StopAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(error);
        Assert.Equal(1, descriptor.Runs);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);

        if (operations)
        {
            var state = await journal!.ReadAsync(Target, TestContext.Current.CancellationToken);
            Assert.NotNull(state);
            Assert.Null(state.Block);
            Assert.Empty(state.Attempts);
            Assert.Equal(0, state.ConsecutiveFailures);
        }
    }

    /// <summary>Repeated scope cleanup failures exhaust the pass retry budget.</summary>
    [Fact]
    public async Task ShouldExhaustRetryBudgetGivenRepeatedScopeCleanupFailure()
    {
        // Arrange
        var disposal = new DisposalProbe();
        var descriptor = new AuditDescriptor();
        await using var provider = Provider(disposal);
        using var worker = Worker(provider, descriptor, attempts: 2);
        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var error = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken));
        await worker.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<WorkloadFailureException>(error);
        Assert.Equal(2, disposal.Count);
    }

    /// <summary>The pass failure remains primary when scope cleanup also fails.</summary>
    [Fact]
    public async Task ShouldRetainHandlerErrorGivenScopeCleanupFailure()
    {
        // Arrange
        var disposal = new DisposalProbe();
        var original = new ProcessorFailureException(
            new CheckpointIdentity("audit", EventStreamPattern.ForPattern("test", "events")),
            ProjectionCheckpoint.Start, ProcessorFailureStage.Handling,
            error: RequestError.Validation("handler failure"));
        var descriptor = new AuditDescriptor(original);
        await using var provider = Provider(disposal);
        using var worker = Worker(provider, descriptor, attempts: 3);
        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var error = await Record.ExceptionAsync(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken));
        await worker.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        var terminal = Assert.IsType<WorkloadFailureException>(error);
        Assert.Same(original, terminal.InnerException);
        var diagnostic = Assert.IsType<ProcessorFailureException>(terminal.InnerException);
        Assert.Contains("scope disposal failed", diagnostic.CleanupException?.ToString());
        Assert.Equal(1, disposal.Count);
    }

    static ServiceProvider Provider(DisposalProbe disposal, MemoryOperationalStore? operations = null)
    {
        var services = new ServiceCollection();
        _ = services.AddPortia();
        _ = services.AddSingleton<IDomainEventReader>(new InMemoryEventStore());
        _ = services.AddScoped(_ => new FailingScopeService(disposal));

        if (operations is not null)
        {
            _ = services.AddSingleton(operations);
            _ = services.AddSingleton<IOperationalStore>(operations);
            _ = services.AddSingleton<IProjectionCheckpointStore>(operations);
            _ = services.AddSingleton<IOperationalReactionCheckpointStore>(operations);
            _ = services.AddSingleton(new OperationalOptions { JournalRetryDelay = TimeSpan.FromMilliseconds(1) });
            _ = services.AddSingleton<IApplicationTopology, EmptyApplicationTopology>();
            _ = services.AddScoped<OperationalExecution>();
        }

        return services.BuildServiceProvider();
    }

    static PortiaWorkloadService Worker(ServiceProvider provider, AuditDescriptor descriptor, int attempts)
    {
        var registration = new WorkloadRegistration(descriptor, "audit", WorkloadScope.Global, options =>
        {
            options.FailureAttemptLimit = attempts;
            options.PollInterval = TimeSpan.FromMilliseconds(1);
            options.MaximumFailureDelay = TimeSpan.FromMilliseconds(5);
        });
        return new PortiaWorkloadService(provider.GetRequiredService<IServiceScopeFactory>(), [registration],
            provider.GetRequiredService<IServiceProviderIsService>(), [new WorkloadLifecycleCoordinator()],
            operationalStore: provider.GetService<IOperationalStore>(),
            operationalOptions: provider.GetService<OperationalOptions>(),
            topology: provider.GetService<IApplicationTopology>());
    }

    static OperationalTarget Target { get; } =
        new("audit", EventStreamPattern.ForPattern("test", "events").ToString(), OperationalScope.Global);

    static ProcessorFailureException Failure(bool transient) => new(
        Target.Identity, ProjectionCheckpoint.Start, ProcessorFailureStage.Handling,
        error: RequestError.Validation("audit failure", transient));

    static async Task<Exception?> WaitForBlockedAsync(
        PortiaWorkloadService worker,
        MemoryOperationalStore journal,
        CancellationToken ct
    )
    {
        while (true)
        {
            if (worker.ExecuteTask is { IsCompleted: true })
                return await Record.ExceptionAsync(() => worker.ExecuteTask);

            if ((await journal.ReadAsync(Target, ct))?.Block is not null)
                return null;

            await Task.Delay(1, ct);
        }
    }

    static async Task WaitAsync(Func<bool> ready, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        while (!ready())
            await Task.Delay(1, timeout.Token);
    }

    static async Task WaitAsync(Func<Task<bool>> ready, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        while (!await ready())
            await Task.Delay(1, timeout.Token);
    }

    sealed class DisposalProbe
    {
        internal bool Fail { get; init; } = true;
        internal int Count;
    }

    sealed class FailingScopeService(DisposalProbe probe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.Count);
            return probe.Fail
                ? ValueTask.FromException(new IOException("scope disposal failed"))
                : ValueTask.CompletedTask;
        }
    }

    sealed class AuditDescriptor(
        Exception? error = null,
        string kind = ProcessorKinds.Projector,
        Func<int, CancellationToken, ValueTask<ProjectionPassResult>>? run = null) : IWorkloadDescriptor
    {
        static EventStreamPattern Source { get; } = EventStreamPattern.ForPattern("test", "events");
        int _runs;

        public Type ComponentType => typeof(AuditDescriptor);
        public bool SupportsRebuild => true;
        public string Kind => kind;
        internal int Runs => Volatile.Read(ref _runs);
        public void Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName) { }
        public EventStreamPattern Pattern(IServiceProvider services) => Source;
        public void Validate(IServiceProvider services, string componentName) { }
        public ValueTask<ProjectionCheckpoint> LoadCheckpoint(
            IServiceProvider services,
            ProjectionRunOptions options,
            CancellationToken ct
        ) => ValueTask.FromResult(ProjectionCheckpoint.Start);
        public ValueTask<ProjectionPassResult> RunPass(
            IServiceProvider services,
            ProjectionRunOptions options,
            CancellationToken ct
        )
        {
            _ = services.GetRequiredService<FailingScopeService>();
            var current = Interlocked.Increment(ref _runs);

            if (run is not null)
                return run(current, ct);

            return error is null
                ? ValueTask.FromResult(default(ProjectionPassResult))
                : ValueTask.FromException<ProjectionPassResult>(error);
        }
        public void Register(IServiceCollection services) { }
    }
}
