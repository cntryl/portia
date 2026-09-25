namespace Cntryl.Portia.Testing;

/// <summary>
///     Runs a projector over given events through Portia's real runner. Construct the projector with
///     <see cref="Store" /> as its <see cref="IProjectionStore" /> and your own read model for its writes, then
///     assert on that read model.
/// </summary>
public sealed class ProjectorScenario
{
    readonly ScenarioHistory _history;
    readonly ScenarioProjectionStore _store = new();

    /// <summary>Creates a scenario for the default test tenant.</summary>
    public ProjectorScenario() : this(new TenantId("scenario")) { }

    /// <summary>Creates a scenario for a specific tenant's streams.</summary>
    /// <param name="tenant">The tenant to bind tenant-scoped projectors to.</param>
    public ProjectorScenario(TenantId tenant) => _history = new ScenarioHistory(tenant);

    /// <summary>Gets the projection store to construct the projector with; it commits progress in memory.</summary>
    public IProjectionStore Store => _store;

    /// <summary>
    ///     Adds events for the projector to receive. Events without metadata are attached to one scenario aggregate
    ///     and numbered in order; events that already carry metadata are delivered as seeded.
    /// </summary>
    /// <param name="events">The events, in delivery order.</param>
    /// <returns>This scenario.</returns>
    public ProjectorScenario Given(params DomainEvent[] events)
    {
        _history.Add(events);
        return this;
    }

    /// <summary>
    ///     Projects every given event not yet committed, resuming from <see cref="Store" />'s checkpoint as a hosted
    ///     projector does. A tenant-scoped projector runs bound to a test tenant.
    /// </summary>
    /// <param name="projector">A projector constructed with <see cref="Store" />.</param>
    /// <param name="ct">A token that can cancel the run.</param>
    /// <returns>A task that completes once every event has been projected and committed.</returns>
    public async Task RunAsync(Projector projector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        if (projector.Pattern.IsTenantTemplate)
            projector.BindWorkload(_history.Workload(projector.Name), null);
        var store = await _history.ToStoreAsync(projector.Pattern, ct).ConfigureAwait(false);
        var identity = new CheckpointIdentity(projector.Name, projector.Pattern);
        var checkpoint = await projector.Store.LoadCheckpointAsync(identity, ct).ConfigureAwait(false);
        _ = await new ProjectorRunner(store).RunAsync(projector, checkpoint, ct: ct).ConfigureAwait(false);
    }
}
