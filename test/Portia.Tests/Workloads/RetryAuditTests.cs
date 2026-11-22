using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Exercises the hosted retry boundary when pass-scope cleanup throws.</summary>
public sealed class RetryAuditTests
{
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

    static ServiceProvider Provider(DisposalProbe disposal)
    {
        var services = new ServiceCollection();
        _ = services.AddPortia();
        _ = services.AddSingleton<IDomainEventReader>(new InMemoryEventStore());
        _ = services.AddScoped(_ => new FailingScopeService(disposal));
        return services.BuildServiceProvider();
    }

    static PortiaWorkloadService Worker(ServiceProvider provider, AuditDescriptor descriptor, int attempts)
    {
        var registration = new WorkloadRegistration(descriptor, "audit", WorkloadScope.Global, options =>
        {
            options.FailureAttemptLimit = attempts;
            options.PollInterval = TimeSpan.FromMilliseconds(1);
        });
        return new PortiaWorkloadService(provider.GetRequiredService<IServiceScopeFactory>(), [registration],
            provider.GetRequiredService<IServiceProviderIsService>(), [new WorkloadLifecycleCoordinator()]);
    }

    sealed class DisposalProbe
    {
        internal int Count;
    }

    sealed class FailingScopeService(DisposalProbe probe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.Count);
            return ValueTask.FromException(new IOException("scope disposal failed"));
        }
    }

    sealed class AuditDescriptor(Exception? error = null) : IWorkloadDescriptor
    {
        static EventStreamPattern Source { get; } = EventStreamPattern.ForPattern("test", "events");
        public Type ComponentType => typeof(AuditDescriptor);
        public bool SupportsRebuild => true;
        public string Kind => ProcessorKinds.Projector;
        public void Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName) { }
        public EventStreamPattern Pattern(IServiceProvider services) => Source;
        public void Validate(IServiceProvider services, string componentName) { }
        public ValueTask<ProjectionCheckpoint> LoadCheckpoint(IServiceProvider services, ProjectionRunOptions options,
            CancellationToken ct) => ValueTask.FromResult(ProjectionCheckpoint.Start);
        public ValueTask<ProjectionPassResult> RunPass(IServiceProvider services, ProjectionRunOptions options,
            CancellationToken ct)
        {
            _ = services.GetRequiredService<FailingScopeService>();
            return error is null
                ? ValueTask.FromResult(default(ProjectionPassResult))
                : ValueTask.FromException<ProjectionPassResult>(error);
        }
        public void Register(IServiceCollection services) { }
    }
}
