namespace Cntryl.Portia.Testing;

/// <summary>
///     Runs a reactor over given events through Portia's real runner and records the requests it sends. Construct
///     the reactor with <see cref="Requests" /> as its <see cref="IRequestBus" />; every no-result request succeeds
///     unless scripted with <see cref="RespondTo{TRequest}" />.
/// </summary>
/// <remarks>
///     The bus records and answers from the script; it dispatches nothing, so no handler, authorizer, guard, or
///     behavior runs. Test a request's own authorization and handling with <see cref="RequestScenario" />.
/// </remarks>
public sealed class ReactorScenario
{
    readonly ScenarioHistory _history;
    readonly ScenarioRequestBus _requests = new();

    /// <summary>Creates a scenario for the default test tenant.</summary>
    public ReactorScenario() : this(new TenantId("scenario"))
    {
    }

    /// <summary>Creates a scenario for a specific tenant's streams.</summary>
    /// <param name="tenant">The tenant to bind tenant-scoped reactors to.</param>
    public ReactorScenario(TenantId tenant) => _history = new ScenarioHistory(tenant);

    /// <summary>Gets the request bus to construct the reactor with.</summary>
    public IRequestBus Requests => _requests;

    /// <summary>Gets every request the reactor sent, in order, across all runs.</summary>
    public IReadOnlyList<IRequestBase> SentRequests => _requests.Sent;

    /// <summary>
    ///     Adds events for the reactor to receive. Events without metadata are attached to one scenario aggregate and
    ///     numbered in order; events that already carry metadata are delivered as seeded.
    /// </summary>
    /// <param name="events">The triggering events, in delivery order.</param>
    /// <returns>This scenario.</returns>
    public ReactorScenario Given(params DomainEvent[] events)
    {
        _history.Add(events);
        return this;
    }

    /// <summary>
    ///     Adds events for the reactor to receive on a chosen stream resource, for a reactor that reads the
    ///     resource of the stream an event came from. The realm and area still come from the reactor's pattern, so
    ///     the same call serves a single-realm and a tenant-scoped reactor. Events without metadata are attached to
    ///     one scenario aggregate per resource and numbered in order.
    /// </summary>
    /// <param name="resource">The resource segment of the stream the events are delivered from.</param>
    /// <param name="events">The events, in delivery order.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="ArgumentException"><paramref name="resource" /> is not a valid stream segment.</exception>
    public ReactorScenario Given(string resource, params DomainEvent[] events)
    {
        _history.Add(resource, events);
        return this;
    }

    /// <summary>Scripts the result every later <typeparamref name="TRequest" /> the reactor sends receives.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="result">The result to return.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TRequest" /> is not a concrete request type.</exception>
    public ReactorScenario RespondTo<TRequest>(Result result) where TRequest : IRequest
    {
        _requests.Script(Concrete<TRequest>(), result);
        return this;
    }

    /// <summary>Scripts the result returned whenever the reactor sends a result-bearing request of this type.</summary>
    /// <typeparam name="TRequest">The concrete request type the response answers.</typeparam>
    /// <typeparam name="TOut">The result the request produces.</typeparam>
    /// <param name="result">The result to return, successful or failed.</param>
    /// <returns>This scenario, for chaining.</returns>
    /// <remarks>An exact script takes precedence over a default for the result type.</remarks>
    public ReactorScenario RespondTo<TRequest, TOut>(Result<TOut> result) where TRequest : IRequest<TOut>
    {
        _requests.Script(Concrete<TRequest>(), result);
        return this;
    }

    /// <summary>Scripts a fallback for result-bearing requests with the specified result type.</summary>
    /// <typeparam name="TOut">The result type.</typeparam>
    /// <param name="result">The result to return, successful or failed.</param>
    /// <returns>This scenario.</returns>
    public ReactorScenario RespondByDefault<TOut>(Result<TOut> result)
    {
        _requests.ScriptDefault(result);
        return this;
    }

    /// <summary>Scripts the items yielded whenever the reactor sends a stream request of this type.</summary>
    /// <typeparam name="TRequest">The concrete request type the response answers.</typeparam>
    /// <typeparam name="TOut">The item the stream yields.</typeparam>
    /// <param name="items">The items to yield, in order.</param>
    /// <returns>This scenario, for chaining.</returns>
    public ReactorScenario RespondToStream<TRequest, TOut>(params TOut[] items) where TRequest : IStreamRequest<TOut>
    {
        ArgumentNullException.ThrowIfNull(items);
        _requests.Script(Concrete<TRequest>(), items.ToArray());
        return this;
    }

    static Type Concrete<TRequest>() => typeof(TRequest).IsAbstract || typeof(TRequest).IsInterface
        ? throw new ArgumentException(
            $"Responses match the request's exact type; '{typeof(TRequest).Name}' is not a concrete request type.",
            nameof(TRequest))
        : typeof(TRequest);

    /// <summary>
    ///     Delivers every given event to <paramref name="reactor" /> from the start of its streams. Running again
    ///     redelivers them, as a reactor sees after a failed checkpoint. A tenant-scoped reactor runs bound to a test
    ///     tenant.
    /// </summary>
    /// <param name="reactor">A reactor constructed with <see cref="Requests" />.</param>
    /// <param name="ct">A token that can cancel the run.</param>
    /// <exception cref="InvalidOperationException">
    ///     Events were given on a stream resource the reactor's pattern excludes.
    /// </exception>
    /// <returns>A task that completes once every event has been reacted to.</returns>
    public async Task RunAsync(Reactor reactor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reactor);
        if (reactor.Pattern.IsTenantTemplate)
            reactor.BindWorkload(_history.Workload(reactor.Name), null);
        var store = await _history.ToStoreAsync(reactor.Pattern, ct).ConfigureAwait(false);
        var runner = new ReactorRunner(store);
        var identity = new CheckpointIdentity(reactor.Name, reactor.Pattern);
        var checkpoint = ProjectionCheckpoint.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Reload authoritative progress before every pass. A committed pass may have
            // observed cancellation before the caller received its result.
            checkpoint = await reactor.Checkpoints.LoadAsync(identity, ct).ConfigureAwait(false);
            var pass = await runner.ExecutePassAsync(null, reactor, checkpoint, ProjectionRunOptions.Default, ct)
                .ConfigureAwait(false);
            checkpoint = pass.Checkpoint;
            if (!pass.ContinueImmediately)
                return;
        }
    }
}
