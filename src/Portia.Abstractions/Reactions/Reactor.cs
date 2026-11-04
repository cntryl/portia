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
        _ = IdentityText.Require(Name, nameof(Name));
    }

    /// <summary>
    ///     Gets the stable checkpoint name. Fixed for the life of this instance once the component
    ///     starts running: hosting replaces it with the name passed to <c>AddReactor</c>, and outside
    ///     hosting it keeps the name given at construction. It is never reassigned afterwards —
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
    ///     Applies the running workload's tenant and, when the workload is hosted, its name.
    ///     <paramref name="componentName" /> is the required <c>name</c> argument of <c>AddReactor</c>,
    ///     and null when the reactor runs outside hosting — the component's own <see cref="Name" /> is
    ///     its checkpoint identity, so a binding that does not name the workload must not silently
    ///     repoint it.
    /// </summary>
    internal void BindWorkload(WorkloadIdentity identity, string? componentName)
    {
        if (componentName is not null)
            _ = IdentityText.Require(componentName, nameof(componentName));
        if (_boundIdentity is { } bound && bound != identity)
        {
            throw new InvalidOperationException(
                $"Reactor '{GetType().FullName}' is already bound to workload '{bound.Name}' and cannot be rebound to workload '{identity.Name}' or another tenant.");
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
            Pattern = Pattern.BindTenant(tenant);
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
    ///     Identity text that makes the legacy field boundaries ambiguous is rejected rather than
    ///     silently assigning a different ID. Reconcile existing effects before migrating such identities.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The reactor's identity is ambiguous in the persisted effect-ID format.</exception>
    /// <exception cref="ArgumentException">The effect name contains malformed UTF-16.</exception>
    protected Uuid CreateEffectId(IReactorContext context, string effectName)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        if (ContainsSourceIdentityDelimiter(Name))
            throw new InvalidOperationException("The reactor name contains an ambiguous legacy effect-ID delimiter; reconcile existing effects before migrating this identity.");
        if (_boundIdentity is { } bound)
        {
            if (bound.Name.Contains(", Tenant = ", StringComparison.Ordinal))
                throw new InvalidOperationException("The workload name contains an ambiguous legacy effect-ID delimiter; reconcile existing effects before migrating this identity.");
            if (bound.Tenant is { } tenant && tenant.Value.Contains(" } }:", StringComparison.Ordinal))
                throw new InvalidOperationException("The tenant contains an ambiguous legacy effect-ID delimiter; reconcile existing effects before migrating this identity.");
        }
        if (HasMalformedUtf16(effectName))
            throw new ArgumentException("An effect name cannot contain malformed UTF-16 text.", nameof(effectName));
        var identity = FormatPersistedWorkloadIdentity(_boundIdentity);
        return Uuid.CreateVersion5(Uuid.UrlNamespace,
            $"portia:effect:{identity}:{Name}:{context.Source.Event.Metadata.EventId}:{effectName}");
    }

    // A longer reactor name can consume another tuple's source UUID and part of its effect name.
    // Match any canonical nonempty UUID, not only this invocation's source, while ordinary colons
    // and a UUID at the start of the name retain their existing persisted derivation.
    static bool ContainsSourceIdentityDelimiter(string name)
    {
        const int uuidLength = 36;
        for (var start = 2; start <= name.Length - uuidLength; start++)
        {
            if (name[start - 1] != ':' || start + uuidLength < name.Length && name[start + uuidLength] != ':')
                continue;
            var candidate = name.AsSpan(start, uuidLength);
            var canonical = true;
            var nonempty = false;
            for (var index = 0; index < candidate.Length; index++)
            {
                var character = candidate[index];
                if (index is 8 or 13 or 18 or 23)
                    canonical &= character == '-';
                else
                {
                    canonical &= character is >= '0' and <= '9' or >= 'a' and <= 'f';
                    nonempty |= character != '0';
                }
            }
            if (canonical && nonempty && !name.AsSpan(0, start - 1).IsWhiteSpace())
                return true;
        }
        return false;
    }

    static bool HasMalformedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsLowSurrogate(character))
                return true;
            if (char.IsHighSurrogate(character)
                && (++index == value.Length || !char.IsLowSurrogate(value[index])))
                return true;
        }
        return false;
    }

    static string FormatPersistedWorkloadIdentity(WorkloadIdentity? identity)
        => identity is null
            ? "unbound"
            : $"WorkloadIdentity {{ Name = {identity.Name}, Tenant = {FormatPersistedTenant(identity.Tenant)} }}";

    static string FormatPersistedTenant(TenantId? tenant)
        => tenant is { } value ? $"TenantId {{ Value = {value.Value} }}" : string.Empty;

    internal ValueTask<Result> ReactAsync(DomainEventRecord record, CancellationToken ct)
        => ReactToEventAsync(record, new ReactionExecutionContext(record, RequestActor.System), ct);

    internal ValueTask<Result> ReactAsync(DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        => ReactToEventAsync(record, context, ct);

    internal ValueTask<Result> ReactAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct) =>
        ReactBatchAsync(contexts, ct);

    /// <summary>Dispatches a triggering event with its own system execution context.</summary>
    /// <param name="record">The triggering event, with its stream position.</param>
    /// <param name="context">The execution the reaction's effects are attributed to.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once the reaction has run.</returns>
    protected virtual ValueTask<Result> ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
        CancellationToken ct)
        => ValueTask.FromResult(Result.Success);

    /// <summary>Handles reactions in source order, preserving each event's causal context.</summary>
    /// <param name="contexts">One context per triggering event, in source order.</param>
    /// <param name="ct">A token that can cancel the operation.</param>
    /// <returns>A task that completes once every reaction has run.</returns>
    protected virtual async ValueTask<Result> ReactBatchAsync(IReadOnlyList<IReactorContext> contexts, CancellationToken ct)
    {
        foreach (var context in contexts)
        {
            var result = await ReactToEventAsync(context.Source, context, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return result;
        }

        return Result.Success;
    }
}
