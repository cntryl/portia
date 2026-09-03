using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
/// Verifies that authorization declared on a request — <see cref="RequiresPermissionAttribute" />
/// (including <c>{Token}</c> interpolation) and <see cref="IRequestAuthorizer{TRequest}" /> — is
/// enforced by the generated request bus before a handler runs, for every dispatch shape
/// (no-result, with-result, streamed). <see cref="GeneratedRequestBus" /> is generated once for
/// the whole test compilation, so every test here builds it through <see cref="TestRequestBus.Create" />,
/// which supplies every handler/authorizer/evaluator the generator actually wired in, overriding
/// only the ones a given test cares about.
/// </summary>
public sealed class RequestAuthorizationTests
{
    /// <summary>
    /// Verifies that a request with no <see cref="RequiresPermissionAttribute" /> and no
    /// registered authorizer dispatches normally — authorization is opt-in, not a tax on every
    /// request.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchUnguardedRequestWithoutConsultingAnyEvaluator()
    {
        var bus = TestRequestBus.Create();

        var result = await bus.SendAsync(new GetValue(), RequestActor.System);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Verifies that a no-result request's declared permission is checked before its handler
    /// runs, and a denial short-circuits dispatch entirely.
    /// </summary>
    [Fact]
    public async Task ShouldBlockNoResultRequestWhenPermissionDenied()
    {
        var handler = new GuardedActionHandler();
        var bus = TestRequestBus.Create(guardedActionHandler: handler, permissionEvaluator: TestPermissionEvaluator.DenyAll());

        var result = await bus.SendAsync(new GuardedAction(), RequestActor.Anonymous);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error!.Kind);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a no-result request's declared permission being granted lets dispatch
    /// through to its handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchNoResultRequestWhenPermissionGranted()
    {
        var handler = new GuardedActionHandler();
        var bus = TestRequestBus.Create(guardedActionHandler: handler, permissionEvaluator: TestPermissionEvaluator.AllowAll());

        var result = await bus.SendAsync(new GuardedAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.True(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a with-result request's declared permission is checked before its handler
    /// runs, and a denial produces a failed <see cref="Result{T}" /> rather than throwing.
    /// </summary>
    [Fact]
    public async Task ShouldBlockWithResultRequestWhenPermissionDenied()
    {
        var bus = TestRequestBus.Create(permissionEvaluator: TestPermissionEvaluator.DenyAll());

        var result = await bus.SendAsync(new GuardedQuery(), RequestActor.Anonymous);

        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Forbidden, result.Error!.Kind);
    }

    /// <summary>
    /// Verifies a <c>{Token}</c> in a RequiresPermission string is substituted with the actual
    /// value off the concrete request instance being dispatched, not a placeholder or the raw
    /// token text — so two requests of the same type with different data are checked against two
    /// different permission strings.
    /// </summary>
    [Fact]
    public async Task ShouldInterpolateRequestPropertyIntoPermissionString()
    {
        var evaluator = TestPermissionEvaluator.AllowAll();
        var bus = TestRequestBus.Create(permissionEvaluator: evaluator);

        _ = await bus.SendAsync(new GetOrder(42), RequestActor.System);
        _ = await bus.SendAsync(new GetOrder(99), RequestActor.System);

        Assert.Equal(["orders:42:read", "orders:99:read"], evaluator.EvaluatedPermissions);
    }

    /// <summary>
    /// Verifies that a registered <see cref="IRequestAuthorizer{TRequest}" /> runs before the
    /// handler and can deny a request that has no <see cref="RequiresPermissionAttribute" /> at
    /// all — the two mechanisms are independent.
    /// </summary>
    [Fact]
    public async Task ShouldBlockRequestWhenAuthorizerDenies()
    {
        var handler = new AuthorizedActionHandler();
        var bus = TestRequestBus.Create(authorizedActionHandler: handler, authorizedActionAuthorizer: new AuthorizedActionAuthorizer());

        var result = await bus.SendAsync(new AuthorizedAction(OwnerId: 8), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a registered <see cref="IRequestAuthorizer{TRequest}" /> receives the actual
    /// request instance (row-level data), not just the request type — granting for one instance
    /// and denying for another of the same type.
    /// </summary>
    [Fact]
    public async Task ShouldGrantOrDenyBasedOnRequestInstanceData()
    {
        var bus = TestRequestBus.Create(authorizedActionAuthorizer: new AuthorizedActionAuthorizer());

        var ownedResult = await bus.SendAsync(new AuthorizedAction(OwnerId: 7), RequestActor.System);
        var deniedResult = await bus.SendAsync(new AuthorizedAction(OwnerId: 8), RequestActor.System);

        Assert.True(ownedResult.IsSuccess);
        Assert.False(deniedResult.IsSuccess);
    }

    /// <summary>
    /// Verifies the documented ordering directly: when a request declares both
    /// <see cref="RequiresPermissionAttribute" /> and an <see cref="IRequestAuthorizer{TRequest}" />,
    /// the permission is evaluated first, and a denial there short-circuits — the authorizer never
    /// runs at all, not just "runs but its result is discarded."
    /// </summary>
    [Fact]
    public async Task ShouldCheckPermissionBeforeAuthorizerAndShortCircuitOnDenial()
    {
        var authorizer = new RecordingGuardedAndAuthorizedActionAuthorizer();
        var bus = TestRequestBus.Create(
            guardedAndAuthorizedActionAuthorizer: authorizer,
            permissionEvaluator: TestPermissionEvaluator.DenyAll());

        var result = await bus.SendAsync(new GuardedAndAuthorizedAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.False(authorizer.WasInvoked);
    }

    /// <summary>
    /// Verifies that when the permission is granted, the authorizer still runs afterward and can
    /// independently deny the request.
    /// </summary>
    [Fact]
    public async Task ShouldRunAuthorizerAfterPermissionIsGranted()
    {
        var authorizer = new RecordingGuardedAndAuthorizedActionAuthorizer(grant: false);
        var bus = TestRequestBus.Create(
            guardedAndAuthorizedActionAuthorizer: authorizer,
            permissionEvaluator: TestPermissionEvaluator.AllowAll());

        var result = await bus.SendAsync(new GuardedAndAuthorizedAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.True(authorizer.WasInvoked);
    }

    /// <summary>
    /// Verifies that a streamed request's authorization failure ends the stream with
    /// <see cref="RequestAuthorizationException" /> before the first item is ever produced —
    /// there's no <see cref="Result" /> to wrap for a stream, so this is the failure channel.
    /// </summary>
    [Fact]
    public async Task ShouldThrowForStreamedRequestWhenPermissionDenied()
    {
        var handler = new GuardedSequenceHandler();
        var bus = TestRequestBus.Create(guardedSequenceHandler: handler, permissionEvaluator: TestPermissionEvaluator.DenyAll());

        var exception = await Assert.ThrowsAsync<RequestAuthorizationException>(async () =>
        {
            await foreach (var _ in bus.StreamAsync(new GuardedSequence(), RequestActor.Anonymous))
            {
            }
        });

        Assert.Equal(RequestErrorKind.Forbidden, exception.Error.Kind);
        Assert.False(handler.WasInvoked);
    }

    /// <summary>
    /// Verifies that a streamed request whose authorization is granted actually produces its
    /// items.
    /// </summary>
    [Fact]
    public async Task ShouldStreamItemsWhenPermissionGranted()
    {
        var bus = TestRequestBus.Create(permissionEvaluator: TestPermissionEvaluator.AllowAll());

        var items = new List<int>();

        await foreach (var item in bus.StreamAsync(new GuardedSequence(), RequestActor.System))
            items.Add(item);

        Assert.Equal([1, 2, 3], items);
    }

}

[RequiresPermission("guarded:action")]
sealed record GuardedAction : IRequest;

sealed class GuardedActionHandler : IRequestHandler<GuardedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}

[RequiresPermission("guarded:query")]
sealed record GuardedQuery : IRequest<int>;

sealed class GuardedQueryHandler : IRequestHandler<GuardedQuery, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GuardedQuery> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(1));
}

[RequiresPermission("orders:{OrderId}:read")]
sealed record GetOrder(int OrderId) : IRequest<int>;

sealed class GetOrderHandler : IRequestHandler<GetOrder, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GetOrder> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(context.Request.OrderId));
}

sealed record AuthorizedAction(int OwnerId) : IRequest;

sealed class AuthorizedActionHandler : IRequestHandler<AuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<AuthorizedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}

// Owns 7; denies anyone else — exercises both grant and deny from the same authorizer using
// the request's own data, without needing two competing authorizers for the same request type
// (which the generator rejects as ambiguous).
sealed class AuthorizedActionAuthorizer : IRequestAuthorizer<AuthorizedAction>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<AuthorizedAction> context, ClaimsPrincipal actor, CancellationToken ct) =>
        ValueTask.FromResult(context.Request.OwnerId == 7
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Not the owner.")));
}

[RequiresPermission("guarded:stream")]
sealed record GuardedSequence : IStreamRequest<int>;

sealed class GuardedSequenceHandler : IStreamRequestHandler<GuardedSequence, int>
{
    public bool WasInvoked { get; private set; }

    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<GuardedSequence> context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        WasInvoked = true;
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}

// Declares both authorization mechanisms at once, to prove the documented ordering between them.
[RequiresPermission("guarded_and_authorized:action")]
sealed record GuardedAndAuthorizedAction : IRequest;

sealed class GuardedAndAuthorizedActionHandler : IRequestHandler<GuardedAndAuthorizedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAndAuthorizedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}

sealed class RecordingGuardedAndAuthorizedActionAuthorizer(bool grant = true) : IRequestAuthorizer<GuardedAndAuthorizedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> AuthorizeAsync(IRequestContext<GuardedAndAuthorizedAction> context, ClaimsPrincipal actor, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(grant
            ? Result.Success
            : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Denied by authorizer.")));
    }
}
