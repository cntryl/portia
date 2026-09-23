using System.Security.Claims;

namespace Cntryl.Portia.Testing;

sealed class ScenarioRequestBus : IRequestBus
{
    readonly Dictionary<Type, Result> _results = [];
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

    public void Script(Type requestType, Result result)
    {
        lock (_gate)
            _results[requestType] = result;
    }

    public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
        CancellationToken ct = default) => ValueTask.FromResult(Result.Success);

    public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
        new(actor, metadata: metadata);

    public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _sent.Add(request);
            return ValueTask.FromResult(_results.GetValueOrDefault(request.GetType(), Result.Success));
        }
    }

    public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(ReactorScenario)} records commands; '{request?.GetType().Name}' expects a result.");

    public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
        RequestDispatchContext context, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(ReactorScenario)} records commands; '{request?.GetType().Name}' is a stream request.");
}
