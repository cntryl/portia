using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace Cntryl.Portia.Testing;

// Records what a reactor sends and answers from the scenario's script. It dispatches nothing: no handler,
// authorizer, guard, or behavior runs, so every answer, including a denial, is one the test scripted.
sealed class ScenarioRequestBus : IRequestBus
{
    readonly Dictionary<Type, object> _responses = [];
    readonly Dictionary<Type, object> _defaultResponses = [];
    readonly List<IRequestBase> _sent = [];

    readonly Lock _gate = new();

    public IReadOnlyList<IRequestBase> Sent
    {
        get
        {
            lock (_gate)
                return [.. _sent];
        }
    }

    public void Script(Type requestType, object response)
    {
        lock (_gate)
            _responses[requestType] = response;
    }

    public void ScriptDefault<TOut>(Result<TOut> response)
    {
        lock (_gate)
            _defaultResponses[typeof(TOut)] = response;
    }

    // A scripted denial is the only authorization outcome the scenario knows, so it is reported here
    // exactly as dispatch would report it; everything else the scenario cannot judge and leaves allowed.
    public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            return ValueTask.FromResult(
                _responses.TryGetValue(request.GetType(), out var response) && response is Result
                {
                    IsSuccess: false, Error.Kind: RequestErrorKind.Forbidden or RequestErrorKind.Unauthorized
                } denied
                    ? denied
                    : Result.Success);
        }
    }

    public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
        new(actor, metadata: metadata);

    public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _sent.Add(request);
            return ValueTask.FromResult(_responses.TryGetValue(request.GetType(), out var response)
                ? (Result)response
                : Result.Success);
        }
    }

    public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _sent.Add(request);
            if (_responses.TryGetValue(request.GetType(), out var response) ||
                _defaultResponses.TryGetValue(typeof(TOut), out response))
                return ValueTask.FromResult((Result<TOut>)response);

            throw Unscripted(request, nameof(ReactorScenario.RespondTo));
        }
    }

    public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
        RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _sent.Add(request);
            return _responses.TryGetValue(request.GetType(), out var response)
                ? Enumerate((TOut[])response, ct)
                : throw Unscripted(request, nameof(ReactorScenario.RespondToStream));
        }
    }

    static async IAsyncEnumerable<TOut> Enumerate<TOut>(TOut[] items, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // A no-result request can default to success, but a value or a sequence cannot be invented.
    static InvalidOperationException Unscripted(IRequestBase request, string method) => new(
        $"{nameof(ReactorScenario)} has no response for '{request.GetType().Name}'. "
        + $"Script one with {nameof(ReactorScenario)}.{method} before running the reactor.");
}
