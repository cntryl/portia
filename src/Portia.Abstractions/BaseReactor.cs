namespace Cntryl.Portia;

/// <summary>Reacts to individual events and saves progress after each successful reaction.
/// Effects and progress are not atomic; reactions must tolerate replay.</summary>
public abstract class BaseReactor
{
    WorkloadIdentity? _boundIdentity;
    /// <summary>Uses constructor-injected persistence for reaction progress.</summary>
    protected BaseReactor(IProjectionCheckpointStore checkpoints, EventStreamPattern pattern, string? name = null)
    {
        Checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Name = name ?? GetType().FullName ?? GetType().Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
    }
    /// <summary>Gets the stable checkpoint name.</summary>
    public string Name { get; private set; }
    /// <summary>Gets the consumed event stream pattern.</summary>
    public EventStreamPattern Pattern { get; private set; }
    internal IProjectionCheckpointStore Checkpoints { get; }
    internal virtual bool IsBatch => false;
    /// <summary>
    /// Applies the running workload's tenant and, when the application explicitly named the
    /// workload, its name. <paramref name="componentName" /> is null unless the registration set
    /// <c>WorkloadOptions.Name</c> — the component's own <see cref="Name" /> is its checkpoint
    /// identity, so a registration that does not name the workload must not silently repoint it.
    /// </summary>
    internal void BindWorkload(WorkloadIdentity identity, string? componentName)
    {
        if (_boundIdentity is { } bound && bound != identity)
            throw new InvalidOperationException($"Reactor '{GetType().FullName}' is already bound to workload '{bound}' and cannot be rebound to '{identity}'.");
        if (_boundIdentity == identity)
            return;
        _boundIdentity = identity;
        if (componentName is not null)
            Name = componentName;
        if (identity.Tenant is { } tenant)
            Pattern = EventStreamPattern.ForPattern(tenant.Value, Pattern.Area, Pattern.Resource);
    }
    /// <summary>Creates a stable idempotency key for one named effect of the source event.</summary>
    protected Uuid CreateEffectId(IReactorContext context, string effectName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        var identity = _boundIdentity?.ToString() ?? "unbound";
        return Uuid.CreateVersion5(Uuid.UrlNamespace, $"portia:effect:{identity}:{Name}:{context.Source.Ev.Metadata.EventId}:{effectName}");
    }
    internal ValueTask ReactAsync(DomainEventRecord record, CancellationToken ct)
        => ReactToEventAsync(record, new ReactionExecutionContext(record, RequestActor.System), ct);
    internal ValueTask ReactAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => ReactToEventAsync(record, context, ct);
    internal ValueTask ReactAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct) => ReactBatchAsync(contexts, ct);
    /// <summary>Dispatches a triggering event with its own system execution context.</summary>
    protected virtual ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => ValueTask.CompletedTask;
    /// <summary>Handles reactions in source order, preserving each event's causal context.</summary>
    protected virtual async ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await ReactToEventAsync(context.Source, context, ct).ConfigureAwait(false);
    }
}

/// <summary>Reacts to bounded batches and saves progress after the complete batch succeeds.
/// External effects can be replayed after failure; batching does not make them transactional.</summary>
public abstract class BaseBatchReactor(IProjectionCheckpointStore checkpoints, EventStreamPattern pattern, string? name = null)
    : BaseReactor(checkpoints, pattern, name)
{
    internal sealed override bool IsBatch => true;
}

/// <summary>Handles a contiguous group of triggering events with independent execution contexts.</summary>
/// <typeparam name="TEvent">The selected event type.</typeparam>
public interface IBatchReactorHandler<TEvent>
{
    /// <summary>Reacts in source order. Each context must be used for effects caused by its event.</summary>
    ValueTask HandleAsync(IReadOnlyList<IReactorContext<TEvent>> contexts, CancellationToken ct);
}
