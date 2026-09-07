namespace Cntryl.Portia;

/// <summary>Projects individual events, atomically committing each event's changes and checkpoint.</summary>
public abstract class BaseProjector
{
    /// <summary>Uses the same repository for application writes and projection progress.</summary>
    protected BaseProjector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Name = name ?? GetType().FullName ?? GetType().Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
    }
    /// <summary>Gets the stable checkpoint name.</summary>
    public string Name { get; private set; }
    /// <summary>Gets the consumed event stream pattern.</summary>
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
        if (componentName is not null)
            Name = componentName;
        if (identity.Tenant is { } tenant)
            Pattern = EventStreamPattern.ForPattern(tenant.Value, Pattern.Area, Pattern.Resource);
    }
    internal ValueTask ProjectAsync(IReadOnlyList<DomainEventRecord> records, CheckpointIdentity identity, CancellationToken ct)
        => ProjectBatchAsync(records, new ProjectorContext(identity), ct);
    /// <summary>Dispatches one event. Generated typed handlers skip unrelated event types.</summary>
    protected virtual ValueTask ProjectEventAsync(DomainEventRecord record, IProjectorContext context, CancellationToken ct)
        => throw new InvalidOperationException($"Projector does not handle event type '{record.Ev.GetType()}'.");
    /// <summary>Dispatches the current unit of work in source order.</summary>
    protected virtual async ValueTask ProjectBatchAsync(IReadOnlyList<DomainEventRecord> records, IProjectorContext context, CancellationToken ct)
    {
        foreach (var record in records)
            await ProjectEventAsync(record, context, ct).ConfigureAwait(false);
    }
}

/// <summary>Projects bounded batches with one atomic data-and-checkpoint commit per batch.
/// Implement batch handler interfaces for bulk writes, or event handlers for ordered application.</summary>
public abstract class BaseBatchProjector(IProjectionStore store, EventStreamPattern pattern, string? name = null)
    : BaseProjector(store, pattern, name)
{
    internal sealed override bool IsBatch => true;
}
