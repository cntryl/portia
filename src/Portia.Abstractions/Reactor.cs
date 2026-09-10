namespace Cntryl.Portia;

/// <summary>
///     Reacts to individual events and saves progress after each successful reaction. Effects and
///     progress are not atomic; reactions must tolerate replay.
///     <para>
///         This is where effects belong — a command dispatch, an integration call, a publish —
///         because they cannot join a projection's transaction. Driving a reactor is Portia's job: the
///         members a runner needs are internal, so <c>ReactorRunner</c> is the only implementation of
///         that role.
///     </para>
/// </summary>
public abstract class Reactor
{
    WorkloadIdentity? _boundIdentity;

    /// <summary>Uses constructor-injected persistence for reaction progress.</summary>
    /// <param name="checkpoints">The store that persists this reactor's progress.</param>
    /// <param name="pattern">The event streams this reactor consumes.</param>
    /// <param name="name">
    ///     The stable checkpoint name, or <see langword="null" /> to use the reactor's
    ///     own type name.
    /// </param>
    protected Reactor(IProjectionCheckpointStore checkpoints, EventStreamPattern pattern, string? name = null)
    {
        Checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Name = name ?? GetType().FullName ?? GetType().Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
    }

    /// <summary>
    ///     Gets the stable checkpoint name. Fixed for the life of this instance once the component
    ///     starts running: a registration that explicitly named the workload supplies that name, and
    ///     otherwise this keeps the name given at construction. It is never reassigned afterwards —
    ///     rebinding to a second workload throws rather than silently repointing the checkpoint this
    ///     component has already been writing.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    ///     Gets the consumed event stream pattern, narrowed to the running workload's tenant when it
    ///     has one. Like <see cref="Name" />, fixed once the component starts running.
    /// </summary>
    public EventStreamPattern Pattern { get; private set; }

    internal IProjectionCheckpointStore Checkpoints { get; }
    internal virtual bool IsBatch => false;

    /// <summary>
    ///     Applies the running workload's tenant and, when the application explicitly named the
    ///     workload, its name. <paramref name="componentName" /> is null unless the registration set
    ///     <c>WorkloadOptions.Name</c> — the component's own <see cref="Name" /> is its checkpoint
    ///     identity, so a registration that does not name the workload must not silently repoint it.
    /// </summary>
    internal void BindWorkload(WorkloadIdentity identity, string? componentName)
    {
        if (_boundIdentity is { } bound && bound != identity)
        {
            throw new InvalidOperationException(
                $"Reactor '{GetType().FullName}' is already bound to workload '{bound}' and cannot be rebound to '{identity}'.");
        }

        if (_boundIdentity == identity)
        {
            return;
        }

        _boundIdentity = identity;
        if (componentName is not null)
        {
            Name = componentName;
        }

        if (identity.Tenant is { } tenant)
        {
            Pattern = EventStreamPattern.ForPattern(tenant.Value, Pattern.Area, Pattern.Resource);
        }
    }

    /// <summary>
    ///     Creates a stable idempotency key for one named effect of the source event. The same
    ///     reactor, tenant, source event, and effect name always produce the same key, so an effect
    ///     replayed after a failed checkpoint can be recognized as one already performed.
    /// </summary>
    /// <param name="context">The reaction whose source event causes the effect.</param>
    /// <param name="effectName">A name distinguishing this effect from others the same reaction causes.</param>
    /// <returns>The idempotency key for this effect of this source event.</returns>
    /// <remarks>
    ///     The workload portion preserves the text emitted by the original persisted-key algorithm.
    ///     Applications store these IDs, so its field order and formatting are a compatibility
    ///     contract independent of future changes to <see cref="WorkloadIdentity" />.
    /// </remarks>
    protected Uuid CreateEffectId(IReactorContext context, string effectName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        var identity = FormatPersistedWorkloadIdentity(_boundIdentity);
        return Uuid.CreateVersion5(Uuid.UrlNamespace,
            $"portia:effect:{identity}:{Name}:{context.Source.Event.Metadata.EventId}:{effectName}");
    }

    static string FormatPersistedWorkloadIdentity(WorkloadIdentity? identity)
        => identity is null
            ? "unbound"
            : $"WorkloadIdentity {{ Name = {identity.Name}, Tenant = {FormatPersistedTenant(identity.Tenant)} }}";

    static string FormatPersistedTenant(TenantId? tenant)
        => tenant is { } value ? $"TenantId {{ Value = {value.Value} }}" : string.Empty;

    internal ValueTask ReactAsync(DomainEventRecord record, CancellationToken ct)
        => ReactToEventAsync(record, new ReactionExecutionContext(record, RequestActor.System), ct);

    internal ValueTask ReactAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => ReactToEventAsync(record, context, ct);

    internal ValueTask ReactAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct) =>
        ReactBatchAsync(contexts, ct);

    /// <summary>Dispatches a triggering event with its own system execution context.</summary>
    /// <param name="record">The triggering event, with its stream position.</param>
    /// <param name="context">The execution the reaction's effects are attributed to.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the reaction has run.</returns>
    protected virtual ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    /// <summary>Handles reactions in source order, preserving each event's causal context.</summary>
    /// <param name="contexts">One context per triggering event, in source order.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once every reaction has run.</returns>
    protected virtual async ValueTask ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
            await ReactToEventAsync(context.Source, context, ct).ConfigureAwait(false);
    }
}
