using System.Security.Claims;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Runs one request through the application's real Portia lifecycle — authorization, pipeline behaviors,
///     guards, and the handler — and asserts on what actually happened. Scenarios and expectations are
///     immutable values; awaiting expectations runs the request once in a fresh service scope.
/// </summary>
public sealed class RequestScenario
{
    readonly ClaimsPrincipal _actor;
    readonly RequestMetadata? _metadata;
    readonly IServiceProvider _services;

    RequestScenario(IServiceProvider services, ClaimsPrincipal actor, RequestMetadata? metadata)
    {
        _services = services;
        _actor = actor;
        _metadata = metadata;
    }

    /// <summary>Starts a scenario against a provider composed with Portia; the actor defaults to anonymous.</summary>
    /// <param name="services">The application's service provider.</param>
    /// <returns>The scenario.</returns>
    public static RequestScenario For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new RequestScenario(services, RequestActor.Anonymous, null);
    }

    /// <summary>Returns a scenario that dispatches as <paramref name="actor" />.</summary>
    /// <param name="actor">The acting principal.</param>
    /// <returns>A new scenario.</returns>
    public RequestScenario GivenActor(ClaimsPrincipal actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new RequestScenario(_services, actor, _metadata);
    }

    /// <summary>Returns a scenario that dispatches with <paramref name="metadata" />.</summary>
    /// <param name="metadata">The request metadata.</param>
    /// <returns>A new scenario.</returns>
    public RequestScenario GivenMetadata(RequestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new RequestScenario(_services, _actor, metadata);
    }

    /// <summary>Describes dispatching a request with no result.</summary>
    /// <param name="request">The request.</param>
    /// <returns>Expectations to add before awaiting.</returns>
    public RequestExpectations When(IRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RequestExpectations(Definition(request, async (bus, context) =>
        {
            try
            {
                var result = await bus.DispatchAsync(request, context).ConfigureAwait(false);
                return (ScenarioOutcome.From(result.IsSuccess, result.Error), result);
            }
            catch (EventStreamConcurrencyException)
            {
                var conflict = ScenarioOutcome.Conflict();
                return (conflict, Result.Failure(conflict.Error!));
            }
        }), []);
    }

    /// <summary>Describes dispatching a request with a result.</summary>
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    /// <param name="request">The request.</param>
    /// <returns>Expectations to add before awaiting.</returns>
    public RequestExpectations<TOut> When<TOut>(IRequest<TOut> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RequestExpectations<TOut>(Definition(request, async (bus, context) =>
        {
            try
            {
                var result = await bus.DispatchAsync(request, context).ConfigureAwait(false);
                return (ScenarioOutcome.From(result.IsSuccess, result.Error), result);
            }
            catch (EventStreamConcurrencyException)
            {
                var conflict = ScenarioOutcome.Conflict();
                return (conflict, Result<TOut>.Failure(conflict.Error!));
            }
        }), []);
    }

    /// <summary>Describes dispatching a streamed request and collecting every item.</summary>
    /// <typeparam name="TOut">The type of each item.</typeparam>
    /// <param name="request">The request.</param>
    /// <returns>Expectations to add before awaiting.</returns>
    public StreamRequestExpectations<TOut> When<TOut>(IStreamRequest<TOut> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new StreamRequestExpectations<TOut>(Definition(request, async (bus, context) =>
        {
            var items = new List<TOut>();
            try
            {
                await foreach (var item in bus.DispatchStreamAsync(request, context).ConfigureAwait(false))
                    items.Add(item);
            }
            catch (RequestAuthorizationException ex)
            {
                return (new ScenarioOutcome(false, ex.Error, ex), items);
            }
            catch (RequestGuardException ex)
            {
                return (new ScenarioOutcome(false, ex.Error, ex), items);
            }
            catch (EventStreamConcurrencyException)
            {
                return (ScenarioOutcome.Conflict(), items);
            }

            return (ScenarioOutcome.From(true, null), (IReadOnlyList<TOut>)items);
        }), []);
    }

    ScenarioDefinition<TPayload> Definition<TPayload>(IRequestBase request,
        Func<IRequestBus, RequestDispatchContext, Task<(ScenarioOutcome Outcome, TPayload Payload)>> dispatch) =>
        new(_services, _actor, _metadata, request, dispatch);
}
