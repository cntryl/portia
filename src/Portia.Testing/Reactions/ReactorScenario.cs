namespace Cntryl.Portia.Testing;

/// <summary>
///     Runs a reactor over given events through Portia's real runner and records the requests it sends. Construct
///     the reactor with <see cref="Requests" /> as its <see cref="IRequestBus" />; every request succeeds unless
///     scripted with <see cref="RespondTo{TRequest}" />.
/// </summary>
public sealed class ReactorScenario
{
    readonly ScenarioHistory _history = new();
    readonly ScenarioRequestBus _requests = new();

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

    /// <summary>Scripts the result every later <typeparamref name="TRequest" /> the reactor sends receives.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="result">The result to return.</param>
    /// <returns>This scenario.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TRequest" /> is not a concrete request type.</exception>
    public ReactorScenario RespondTo<TRequest>(Result result) where TRequest : IRequest
    {
        if (typeof(TRequest).IsAbstract || typeof(TRequest).IsInterface)
        {
            throw new ArgumentException(
                $"Responses match the request's exact type; '{typeof(TRequest).Name}' is not a concrete request type.",
                nameof(TRequest));
        }

        _requests.Script(typeof(TRequest), result);
        return this;
    }

    /// <summary>
    ///     Delivers every given event to <paramref name="reactor" /> from the start of its streams. Running again
    ///     redelivers them, as a reactor sees after a failed checkpoint. A tenant-scoped reactor runs bound to a test
    ///     tenant.
    /// </summary>
    /// <param name="reactor">A reactor constructed with <see cref="Requests" />.</param>
    /// <param name="ct">A token that can cancel the run.</param>
    /// <returns>A task that completes once every event has been reacted to.</returns>
    public async Task RunAsync(Reactor reactor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reactor);
        if (reactor.Pattern.IsTenantTemplate)
            reactor.BindWorkload(ScenarioHistory.Workload(reactor.Name), null);
        var store = await _history.ToStoreAsync(reactor.Pattern, ct).ConfigureAwait(false);
        _ = await new ReactorRunner(store).RunAsync(reactor, ProjectionCheckpoint.Start, ct: ct).ConfigureAwait(false);
    }
}
