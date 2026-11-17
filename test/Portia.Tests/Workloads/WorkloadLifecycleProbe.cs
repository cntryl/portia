using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

sealed class WorkloadLifecycleDescriptor(Func<CancellationToken, ValueTask<ProjectionPassResult>> pass,
    Func<EventStreamPattern>? pattern = null) : IWorkloadDescriptor
{
    public Type ComponentType => typeof(WorkloadLifecycleDescriptor);
    public bool SupportsRebuild => true;
    public string Kind => "projector";
    public void Bind(IServiceProvider services, WorkloadIdentity identity, string? componentName) { }
    public EventStreamPattern Pattern(IServiceProvider services) => pattern?.Invoke() ?? EventStreamPattern.ForPattern("test", "events");
    public void Validate(IServiceProvider services, string componentName) { }
    public ValueTask<ProjectionCheckpoint> LoadCheckpoint(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct) => ValueTask.FromResult(default(ProjectionCheckpoint));
    public ValueTask<ProjectionPassResult> RunPass(IServiceProvider services, ProjectionRunOptions options, CancellationToken ct) => pass(ct);
    public void Register(IServiceCollection services) { }
}

sealed class WorkloadLifecycleCoordinator : IWorkloadCoordinator
{
    public Task RunAsync(Func<IReadOnlyCollection<WorkloadIdentity>> workloads,
        Func<WorkloadIdentity, CancellationToken, Task> run, CancellationToken ct = default)
        => run(workloads().Single(), ct);
}

sealed class WorkloadLifecycleNotifier(bool throwOnDispose, bool throwOnSubscribe = false) : IDomainEventNotifier
{
    internal IOException CleanupFailure { get; } = new("subscription disposal failed");
    public ValueTask<IDomainEventSubscription> SubscribeAsync(EventStreamPattern pattern, CancellationToken ct = default)
        => throwOnSubscribe ? ValueTask.FromException<IDomainEventSubscription>(new IOException("subscription unavailable"))
            : ValueTask.FromResult<IDomainEventSubscription>(new Subscription(this, throwOnDispose));

    sealed class Subscription(WorkloadLifecycleNotifier owner, bool throwOnDispose) : IDomainEventSubscription
    {
        public ValueTask WaitAsync(CancellationToken ct = default) => new(Task.Delay(Timeout.InfiniteTimeSpan, ct));
        public ValueTask DisposeAsync() => throwOnDispose ? ValueTask.FromException(owner.CleanupFailure) : ValueTask.CompletedTask;
    }
}

static class WorkloadLifecycleTestHost
{
    internal static ServiceProvider Create(ITenantDirectory? directory = null)
    {
        var services = new ServiceCollection();
        _ = services.AddPortia();
        if (directory is not null)
            _ = services.AddSingleton(directory);
        _ = services.AddSingleton<IDomainEventReader>(new InMemoryEventStore());
        return services.BuildServiceProvider();
    }

    internal static PortiaWorkloadService Worker(ServiceProvider provider, WorkloadLifecycleDescriptor descriptor,
        IDomainEventNotifier? notifier = null, IWorkloadCoordinator? coordinator = null, ITenantDirectory? directory = null)
    {
        var registration = new WorkloadRegistration(descriptor, "test", directory is null ? WorkloadScope.Global : WorkloadScope.PerTenant,
            options => { options.PollInterval = TimeSpan.FromMilliseconds(10); options.FailureAttemptLimit = 1; });
        return new(provider.GetRequiredService<IServiceScopeFactory>(), [registration], provider.GetRequiredService<IServiceProviderIsService>(),
            [coordinator ?? new WorkloadLifecycleCoordinator()], directory, notifier);
    }
}
