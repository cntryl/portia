namespace Cntryl.Portia;

/// <summary>
/// Verifies that requests delivered live (Fitz notice fanout, a fired Fitz schedule entry)
/// dispatch through the same request bus as any other origin, and that this transport's
/// no-redelivery guarantee — a failed dispatch is simply lost, never retried — holds for both an
/// ordinary handler failure and an actor token that fails re-validation.
/// </summary>
public sealed class LiveRequestRunnerTests
{
    /// <summary>
    /// Verifies that every delivered request is dispatched to its handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchEveryDeliveredRequest()
    {
        var handler = new ChangeValueHandler();
        var bus = TestRequestBus.Create(changeValueHandler: handler);
        var consumer = new FakeLiveRequestConsumer([
            new LiveRequest(new ChangeValue(1), ActorToken: null),
            new LiveRequest(new ChangeValue(2), ActorToken: null),
            new LiveRequest(new ChangeValue(3), ActorToken: null),
        ]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator());

        await runner.RunAsync();

        Assert.Equal(3, handler.LastValue);
    }

    /// <summary>
    /// Verifies that a dispatch failure doesn't stop the run — later deliveries still dispatch,
    /// matching this transport's "simply lost, not retried" guarantee (there's nothing to
    /// abandon or redeliver at this level).
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterDispatchFailure()
    {
        var handler = new ChangeValueHandler();
        var bus = TestRequestBus.Create(changeValueHandler: handler);
        var consumer = new FakeLiveRequestConsumer([
            new LiveRequest(new ThrowingChangeValue(0), ActorToken: null),
            new LiveRequest(new ChangeValue(2), ActorToken: null),
        ]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator());

        await runner.RunAsync();

        Assert.Equal(2, handler.LastValue);
    }

    /// <summary>
    /// Verifies that a request whose carried actor token fails re-validation — e.g. it expired
    /// since it was scheduled or published — never reaches its handler. Expiry is honored the
    /// same way here as it is on the queue path, even though there's no redelivery decision to
    /// make for this transport shape.
    /// </summary>
    [Fact]
    public async Task ShouldSkipDispatchWhenActorTokenFailsRevalidation()
    {
        var handler = new ChangeValueHandler();
        var bus = TestRequestBus.Create(changeValueHandler: handler);
        var consumer = new FakeLiveRequestConsumer([
            new LiveRequest(new ChangeValue(1), ActorToken: "expired-token"),
        ]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator(rejectToken: "expired-token"));

        await runner.RunAsync();

        Assert.Null(handler.LastValue);
    }

    /// <summary>
    /// Verifies that a request whose actor token fails re-validation doesn't stop the run — the
    /// deliveries that follow it still dispatch normally.
    /// </summary>
    [Fact]
    public async Task ShouldContinueAfterActorTokenRevalidationFailure()
    {
        var handler = new ChangeValueHandler();
        var bus = TestRequestBus.Create(changeValueHandler: handler);
        var consumer = new FakeLiveRequestConsumer([
            new LiveRequest(new ChangeValue(1), ActorToken: "expired-token"),
            new LiveRequest(new ChangeValue(2), ActorToken: "valid-token"),
        ]);
        var runner = new LiveRequestRunner(consumer, bus, new FakeRequestActorValidator(rejectToken: "expired-token"));

        await runner.RunAsync();

        Assert.Equal(2, handler.LastValue);
    }

    sealed class FakeLiveRequestConsumer(IReadOnlyList<LiveRequest> items) : ILiveRequestConsumer
    {
        public async IAsyncEnumerable<LiveRequest> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class FakeRequestActorValidator(string? rejectToken = null) : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(token is not null && token == rejectToken
                ? Result<System.Security.Claims.ClaimsPrincipal>.Failure(new RequestError(RequestErrorKind.Unauthorized, "Token expired."))
                : Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed record ThrowingChangeValue(int Value) : IRequest;
}
