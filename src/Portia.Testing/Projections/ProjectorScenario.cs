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
    public ProjectorScenario() : this(new TenantId("scenario"))
    {
    }

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
    ///     Adds events for the projector to receive on a chosen stream resource, for a projector that reads the
    ///     resource of the stream an event came from. The realm and area still come from the projector's pattern, so
    ///     the same call serves a single-realm and a tenant-scoped projector. Events without metadata are attached to
    ///     one scenario aggregate per resource and numbered in order.
    /// </summary>
    /// <param name="resource">The resource segment of the stream the events are delivered from.</param>
    /// <param name="events">The events, in delivery order.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="ArgumentException"><paramref name="resource" /> is not a valid stream segment.</exception>
    public ProjectorScenario Given(string resource, params DomainEvent[] events)
    {
        _history.Add(resource, events);
        return this;
    }

    /// <summary>Runs a constructed application projector through its generated dispatch.</summary>
    /// <typeparam name="T">The application projector type.</typeparam>
    /// <param name="projector">The projector and its repository.</param>
    /// <param name="name">The checkpoint name.</param>
    /// <param name="source">The source pattern.</param>
    /// <param name="ct">A token that can cancel the run.</param>
    /// <returns>The completed run.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026", Justification = "The plain projector overload requires its name and source.")]
    public Task RunAsync<T>(T projector, string name, EventStreamPattern source, CancellationToken ct = default)
        where T : class, IProjector => RunAsync(ProjectorDispatch<T>.Create(projector, name, source), ct);

    /// <summary>
    ///     Projects every given event not yet committed, resuming from <see cref="Store" />'s checkpoint as a hosted
    ///     projector does. A tenant-scoped projector runs bound to a test tenant.
    /// </summary>
    /// <param name="projector">A projector constructed with <see cref="Store" />.</param>
    /// <param name="ct">A token that can cancel the run.</param>
    /// <exception cref="InvalidOperationException">
    ///     Events were given on a stream resource the projector's pattern excludes.
    /// </exception>
    /// <returns>A task that completes once every event has been projected and committed.</returns>
    public async Task RunAsync(Projector projector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projector);
        if (projector.Pattern.IsTenantTemplate)
            projector.BindWorkload(_history.Workload(projector.Name), null);
        var store = await _history.ToStoreAsync(projector.Pattern, ct).ConfigureAwait(false);
        var identity = new CheckpointIdentity(projector.Name, projector.Pattern);
        var checkpoint = await projector.Store.LoadCheckpointAsync(identity, ct).ConfigureAwait(false);
        var runner = new ProjectorRunner(store);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await runner.RunPassAsync(null, projector, checkpoint, ct: ct).ConfigureAwait(false);
            checkpoint = pass.Checkpoint;
            if (!pass.ContinueImmediately)
                return;
        }
    }
}
