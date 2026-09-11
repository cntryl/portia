namespace Cntryl.Portia;

/// <summary>Creates delivery-scope factories for directly constructed runners.</summary>
public static class RequestDeliveryScopes
{
    /// <summary>Uses the supplied notification dependencies for every delivery.</summary>
    /// <param name="bus">The bus that executes validated requests.</param>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="timeProvider">
    ///     The clock request contexts are stamped with, or
    ///     <see langword="null" /> to use <see cref="TimeProvider.System" />.
    /// </param>
    /// <returns>A factory handing out one independently disposable scope per delivery.</returns>
    public static IRequestDeliveryScopeFactory Fixed(IRequestBus bus, IRequestActorValidator actorValidator,
        TimeProvider? timeProvider = null) => new FixedFactory(bus, actorValidator, timeProvider);

    /// <summary>Uses the supplied queue dependencies and terminal policy for every delivery.</summary>
    /// <param name="bus">The bus that executes validated requests.</param>
    /// <param name="actorValidator">The transport-boundary actor-token validator.</param>
    /// <param name="options">The queue runner settings, or <see langword="null" /> for the defaults.</param>
    /// <param name="terminalHandler">
    ///     Runs before a terminal delivery is acknowledged. When <see langword="null" />, a
    ///     terminal delivery faults the runner and remains transport-owned.
    /// </param>
    /// <param name="timeProvider">
    ///     The clock request contexts are stamped with, or
    ///     <see langword="null" /> to use <see cref="TimeProvider.System" />.
    /// </param>
    /// <returns>A factory handing out one independently disposable scope per delivery.</returns>
    public static IQueueDeliveryScopeFactory FixedQueue(IRequestBus bus, IRequestActorValidator actorValidator,
        QueueRunnerOptions? options = null, IQueuedRequestTerminalHandler? terminalHandler = null,
        TimeProvider? timeProvider = null) => new FixedQueueFactory(
        bus, actorValidator, timeProvider, options ?? new QueueRunnerOptions(), terminalHandler);

    // Each call returns its own scope, because the port promises one independently disposable
    // scope per delivery. Handing out one shared instance would work only for as long as
    // disposal stayed a no-op, and would then silently break a caller that trusted the contract.
    sealed class FixedFactory(IRequestBus bus, IRequestActorValidator actorValidator, TimeProvider? timeProvider)
        : IRequestDeliveryScopeFactory
    {
        public ValueTask<IRequestDeliveryScope> CreateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IRequestDeliveryScope>(new FixedScope(bus, actorValidator, timeProvider));
    }

    sealed class FixedQueueFactory(
        IRequestBus bus,
        IRequestActorValidator actorValidator,
        TimeProvider? timeProvider,
        QueueRunnerOptions options,
        IQueuedRequestTerminalHandler? terminalHandler) : IQueueDeliveryScopeFactory
    {
        public ValueTask<IQueueDeliveryScope> CreateAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IQueueDeliveryScope>(
                new FixedQueueScope(bus, actorValidator, timeProvider, options, terminalHandler));
    }

    class FixedScope(IRequestBus bus, IRequestActorValidator actorValidator, TimeProvider? timeProvider)
        : IRequestDeliveryScope
    {
        public IRequestBus Bus { get; } = bus ?? throw new ArgumentNullException(nameof(bus));

        public IRequestActorValidator ActorValidator { get; } =
            actorValidator ?? throw new ArgumentNullException(nameof(actorValidator));

        public TimeProvider? TimeProvider { get; } = timeProvider;

        /// <summary>Releases nothing: the dependencies are owned by the caller that supplied them.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FixedQueueScope(
        IRequestBus bus,
        IRequestActorValidator actorValidator,
        TimeProvider? timeProvider,
        QueueRunnerOptions options,
        IQueuedRequestTerminalHandler? terminalHandler)
        : FixedScope(bus, actorValidator, timeProvider), IQueueDeliveryScope
    {
        public QueueRunnerOptions Options { get; } = options;
        public IQueuedRequestTerminalHandler? TerminalHandler { get; } = terminalHandler;
    }
}
