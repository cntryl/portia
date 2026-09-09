using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Portia;

/// <summary>Owns the application dependencies used for one notification delivery.</summary>
public interface IRequestDeliveryScope : IAsyncDisposable
{
    /// <summary>Gets the request bus for this delivery.</summary>
    IRequestBus Bus { get; }
    /// <summary>Gets the actor validator for this delivery.</summary>
    IRequestActorValidator ActorValidator { get; }
    /// <summary>Gets the application time provider, when configured.</summary>
    TimeProvider? TimeProvider { get; }
}

/// <summary>Creates one independently disposable notification delivery scope.</summary>
public interface IRequestDeliveryScopeFactory
{
    /// <summary>Creates the scope for one delivered request.</summary>
    ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default);
}

/// <summary>Owns the application dependencies and terminal policy used for one queue delivery.</summary>
public interface IQueueDeliveryScope : IRequestDeliveryScope
{
    /// <summary>Gets the terminal-attempt policy.</summary>
    QueueRunnerOptions Options { get; }
    /// <summary>Gets the optional terminal-failure handler.</summary>
    IQueuedRequestTerminalHandler? TerminalHandler { get; }
}

/// <summary>Creates one independently disposable queue delivery scope.</summary>
public interface IQueueDeliveryScopeFactory
{
    /// <summary>Creates the scope for one reserved request.</summary>
    ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default);
}

/// <summary>Creates delivery-scope factories for directly constructed runners.</summary>
public static class RequestDeliveryScopes
{
    /// <summary>Uses the supplied notification dependencies for every delivery.</summary>
    public static IRequestDeliveryScopeFactory Fixed(IRequestBus bus, IRequestActorValidator actorValidator,
        TimeProvider? timeProvider = null) => new FixedFactory(new FixedScope(bus, actorValidator, timeProvider));

    /// <summary>Uses the supplied queue dependencies and terminal policy for every delivery.</summary>
    public static IQueueDeliveryScopeFactory FixedQueue(IRequestBus bus, IRequestActorValidator actorValidator,
        QueueRunnerOptions? options = null, IQueuedRequestTerminalHandler? terminalHandler = null,
        TimeProvider? timeProvider = null) => new FixedQueueFactory(
            new FixedQueueScope(bus, actorValidator, timeProvider, options ?? new QueueRunnerOptions(), terminalHandler));

    sealed class FixedFactory(IRequestDeliveryScope scope) : IRequestDeliveryScopeFactory
    {
        public ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(scope);
    }

    sealed class FixedQueueFactory(IQueueDeliveryScope scope) : IQueueDeliveryScopeFactory
    {
        public ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(scope);
    }

    class FixedScope(IRequestBus bus, IRequestActorValidator actorValidator, TimeProvider? timeProvider) : IRequestDeliveryScope
    {
        public IRequestBus Bus { get; } = bus ?? throw new ArgumentNullException(nameof(bus));
        public IRequestActorValidator ActorValidator { get; } = actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));
        public TimeProvider? TimeProvider { get; } = timeProvider;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FixedQueueScope(IRequestBus bus, IRequestActorValidator actorValidator, TimeProvider? timeProvider,
        QueueRunnerOptions options, IQueuedRequestTerminalHandler? terminalHandler)
        : FixedScope(bus, actorValidator, timeProvider), IQueueDeliveryScope
    {
        public QueueRunnerOptions Options { get; } = options;
        public IQueuedRequestTerminalHandler? TerminalHandler { get; } = terminalHandler;
    }
}

sealed class DependencyInjectionRequestDeliveryScopeFactory(IServiceScopeFactory scopes) : IRequestDeliveryScopeFactory
{
    public ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IRequestDeliveryScope>(new DependencyInjectionRequestDeliveryScope(scopes.CreateAsyncScope()));
}

sealed class DependencyInjectionQueueDeliveryScopeFactory(IServiceScopeFactory scopes) : IQueueDeliveryScopeFactory
{
    public ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IQueueDeliveryScope>(new DependencyInjectionQueueDeliveryScope(scopes.CreateAsyncScope()));
}

class DependencyInjectionRequestDeliveryScope(AsyncServiceScope scope) : IRequestDeliveryScope
{
    protected IServiceProvider Services => scope.ServiceProvider;
    public IRequestBus Bus => Services.GetRequiredService<IRequestBus>();
    public IRequestActorValidator ActorValidator => Services.GetRequiredService<IRequestActorValidator>();
    public TimeProvider? TimeProvider => Services.GetService<TimeProvider>();
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

sealed class DependencyInjectionQueueDeliveryScope(AsyncServiceScope scope)
    : DependencyInjectionRequestDeliveryScope(scope), IQueueDeliveryScope
{
    public QueueRunnerOptions Options => Services.GetService<IOptions<QueueRunnerOptions>>()?.Value ?? new QueueRunnerOptions();
    public IQueuedRequestTerminalHandler? TerminalHandler => Services.GetService<IQueuedRequestTerminalHandler>();
}
