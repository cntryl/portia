namespace Cntryl.Portia;

/// <summary>
/// Verifies generated request dispatch through the request bus.
/// </summary>
public sealed class RequestBusTests
{
    /// <summary>
    /// Verifies that a no-result request is dispatched to its single registered handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchRequestWithNoResult()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(changeValueHandler: handler);
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new ChangeValue(42), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, handler.LastValue);
    }

    /// <summary>
    /// Verifies that a request with a declared result type is dispatched and returns the
    /// handler's result.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchRequestWithResult()
    {
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;

        var result = await bus.SendAsync(new GetValue(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }
}

/// <summary>
/// A request with no result, used to verify request-bus dispatch.
/// </summary>
public sealed record ChangeValue(int Value) : IRequest;

/// <summary>
/// A request with a declared result, used to verify request-bus dispatch.
/// </summary>
public sealed record GetValue : IRequest<int>;

sealed class ChangeValueHandler : IRequestHandler<ChangeValue>
{
    public int? LastValue { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<ChangeValue> context, CancellationToken ct)
    {
        LastValue = context.Request.Value;
        return ValueTask.FromResult(Result.Success);
    }
}

sealed class GetValueHandler : IRequestHandler<GetValue, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GetValue> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(7));
}
