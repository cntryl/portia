namespace Cntryl.Portia;

/// <summary>
/// Projects individual events, atomically committing each event's changes and checkpoint.
///
/// <para>A projector is a pure function of events into its own <see cref="IProjectionStore" />:
/// its writes and its checkpoint share one transaction, so any effect it causes outside that
/// transaction is repeated on every failed commit and every rebuild. Dispatching a request,
/// calling a remote service, publishing, or enqueuing belongs in <see cref="Reactor" />,
/// which is checkpointed separately for exactly that reason. <c>PORTIA100</c> warns when a
/// projector takes a dependency capable of causing an effect.</para>
///
/// <para>Driving a projector is Portia's job: the members a runner needs are internal, so
/// <c>ProjectorRunner</c> is the only implementation of that role. Subclass this to write a
/// projector; do not expect to write an alternative runner.</para>
/// </summary>
public abstract class Projector
{
    WorkloadIdentity? _boundIdentity;
    /// <summary>Uses the same repository for application writes and projection progress.</summary>
    protected Projector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Name = name ?? GetType().FullName ?? GetType().Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
    }
    /// <summary>
    /// Gets the stable checkpoint name. Fixed for the life of this instance once the component
    /// starts running: a registration that explicitly named the workload supplies that name, and
    /// otherwise this keeps the name given at construction. It is never reassigned afterwards —
    /// rebinding to a second workload throws rather than silently repointing the checkpoint this
    /// component has already been writing.
    /// </summary>
    public string Name { get; private set; }
    /// <summary>
    /// Gets the consumed event stream pattern, narrowed to the running workload's tenant when it
    /// has one. Like <see cref="Name" />, fixed once the component starts running.
    /// </summary>
    public EventStreamPattern Pattern { get; private set; }
    internal IProjectionStore Store { get; }
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
            throw new InvalidOperationException($"Projector '{GetType().FullName}' is already bound to workload '{bound}' and cannot be rebound to '{identity}'.");
        if (_boundIdentity == identity)
            return;
        _boundIdentity = identity;
        if (componentName is not null)
            Name = componentName;
        if (identity.Tenant is { } tenant)
            Pattern = EventStreamPattern.ForPattern(tenant.Value, Pattern.Area, Pattern.Resource);
    }
    internal ValueTask ProjectAsync(IReadOnlyList<DomainEventRecord> records, CheckpointIdentity identity, CancellationToken ct)
        => ProjectBatchAsync(records, new ProjectorContext(identity), ct);
    /// <summary>Dispatches one event. Generated typed handlers skip unrelated event types.</summary>
    protected virtual ValueTask ProjectEventAsync(DomainEventRecord record, IProjectorContext context, CancellationToken ct)
        => ValueTask.CompletedTask;
    /// <summary>Dispatches the current unit of work in source order.</summary>
    protected virtual async ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records, IProjectorContext context, CancellationToken ct)
    {
        foreach (var record in records)
            await ProjectEventAsync(record, context, ct).ConfigureAwait(false);
    }
}

/// <summary>Projects bounded batches with one atomic data-and-checkpoint commit per batch.
/// Implement batch handler interfaces for bulk writes, or event handlers for ordered application.</summary>
public abstract class BatchProjector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    : Projector(store, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
