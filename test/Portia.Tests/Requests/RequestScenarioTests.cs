using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="RequestScenario" /> runs the real request lifecycle and asserts on what actually
///     happened: which authorization ran, which guards passed or failed, whether the handler ran, and the result.
/// </summary>
public sealed class RequestScenarioTests
{
    static readonly ClaimsPrincipal Member = RequestActor.CreateSystem("member", "tests");

    /// <summary>An authorized, guarded command that is handled passes every lifecycle expectation.</summary>
    [Fact]
    public async Task ShouldPassWhenAuthorizedGuardedCommandIsHandledSuccessfully()
    {
        await using var provider = Provider();

        var result = await RequestScenario.For(provider)
            .GivenActor(Member)
            .When(new ScenarioCommand())
            .ExpectAuthorized()
            .ExpectGuardPassed<SlugGuard>()
            .ExpectHandled()
            .ExpectSuccess();

        Assert.True(result.IsSuccess);
    }

    /// <summary>A failing guard short-circuits the handler, and both facts are assertable.</summary>
    [Fact]
    public async Task ShouldPassWhenGuardFailsWithExpectedKindAndRequestIsNotHandled()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);

        await RequestScenario.For(provider)
            .GivenActor(Member)
            .When(new ScenarioCommand())
            .ExpectAuthorized()
            .ExpectGuardFailed<SlugGuard>(RequestErrorKind.Conflict)
            .ExpectNotHandled()
            .ExpectFailure(RequestErrorKind.Conflict);
    }

    /// <summary>An unmet expectation names what was expected and shows the observed lifecycle.</summary>
    [Fact]
    public async Task ShouldReportLifecycleTraceWhenExpectHandledButGuardFails()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);

        var error = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand()).ExpectHandled());

        Assert.Contains(nameof(ScenarioCommandHandler), error.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"authorizer {nameof(ScenarioAuthorizer)} -> behavior {nameof(ScenarioBehavior)} -> guard {nameof(SlugGuard)}",
            error.Message, StringComparison.Ordinal);
        Assert.Contains("failure(Conflict)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An authorizer rejection is a denial that stops before guards and the handler.</summary>
    [Fact]
    public async Task ShouldReportDeniedWhenAuthorizerRejects()
    {
        await using var provider = Provider();

        await RequestScenario.For(provider)
            .GivenActor(RequestActor.Anonymous)
            .When(new ScenarioCommand())
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled();
    }

    /// <summary>A permission rejection is a denial too.</summary>
    [Fact]
    public async Task ShouldReportDeniedWhenPermissionEvaluatorRejects()
    {
        await using var provider = Provider(permissions: TestPermissionEvaluator.DenyAll());

        await RequestScenario.For(provider)
            .GivenActor(Member)
            .When(new ScenarioPermissionCommand())
            .ExpectDenied(RequestErrorKind.Forbidden)
            .ExpectNotHandled();
    }

    /// <summary>A guard that fails with Forbidden is a guard failure, not an authorization denial.</summary>
    [Fact]
    public async Task ShouldNotTreatForbiddenGuardFailureAsDenied()
    {
        await using var provider = Provider(RequestErrorKind.Forbidden);

        var error = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand()).ExpectDenied());

        Assert.Contains("denied", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ExpectAuthorized requires authorization to have actually been consulted.</summary>
    [Fact]
    public async Task ShouldFailExpectAuthorizedWhenNothingWasConsulted()
    {
        await using var provider = Provider();

        var error = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioOpenCommand())
                .ExpectAuthorized());

        Assert.Contains("authoriz", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A result-bearing request's value is assertable and returned.</summary>
    [Fact]
    public async Task ShouldAssertQueryValue()
    {
        await using var provider = Provider();

        var result = await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioQuery())
            .ExpectHandled()
            .ExpectSuccess(42);
        var mismatch = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioQuery()).ExpectSuccess(7));

        Assert.Equal(42, result.Value);
        Assert.Contains("42", mismatch.Message, StringComparison.Ordinal);
    }

    /// <summary>Awaiting the same expectations twice runs the request once.</summary>
    [Fact]
    public async Task ShouldRunScenarioOnceWhenAwaitedTwice()
    {
        await using var provider = Provider();
        var log = provider.GetRequiredService<ScenarioLog>();
        var expectations = RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand()).ExpectHandled();

        _ = await expectations;
        _ = await expectations;

        Assert.Equal(1, log.Handled);
    }

    /// <summary>Expectations are immutable values, so extending one leaves the original unchanged.</summary>
    [Fact]
    public async Task ShouldLeaveEarlierExpectationsUnchangedWhenExtended()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);
        var failing = RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand());

        var extended = failing.ExpectHandled();

        var result = await failing;
        Assert.False(result.IsSuccess);
        Assert.Equal(RequestErrorKind.Conflict, result.Error.Kind);
        await Assert.ThrowsAsync<ScenarioExpectationException>(async () => await extended);
    }

    /// <summary>The scenario and its actor are immutable too.</summary>
    [Fact]
    public async Task ShouldKeepBaseScenarioUnchangedWhenGivenActor()
    {
        await using var provider = Provider();
        var scenario = RequestScenario.For(provider);

        await scenario.GivenActor(Member).When(new ScenarioCommand()).ExpectAuthorized().ExpectHandled();
        await scenario.When(new ScenarioCommand()).ExpectDenied(RequestErrorKind.Forbidden);
    }

    /// <summary>A guard expectation for a guard that cannot apply to the request says so.</summary>
    [Fact]
    public async Task ShouldRejectGuardExpectationForGuardNotApplicableToRequest()
    {
        await using var provider = Provider();

        var error = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand())
                .ExpectGuardPassed<OpenCommandGuard>());

        Assert.Contains($"{nameof(OpenCommandGuard)} is not a guard registered for {nameof(ScenarioCommand)}",
            error.Message, StringComparison.Ordinal);
    }

    /// <summary>Each run resolves scoped components from its own scope.</summary>
    [Fact]
    public async Task ShouldResolveScopedComponentsFromFreshScopePerRun()
    {
        await using var provider = Provider();
        var log = provider.GetRequiredService<ScenarioLog>();

        await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand()).ExpectHandled();
        await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand()).ExpectHandled();

        Assert.Equal(2, log.Scopes.Distinct().Count());
    }

    /// <summary>Every unmet expectation is reported together.</summary>
    [Fact]
    public async Task ShouldReportAllUnmetExpectationsTogether()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);

        var error = await Assert.ThrowsAsync<ScenarioExpectationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand())
                .ExpectHandled()
                .ExpectSuccess());

        Assert.Contains("2 expectation", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unexpected exception from the lifecycle propagates unchanged.</summary>
    [Fact]
    public async Task ShouldPropagateDispatchException()
    {
        await using var provider = Provider();

        _ = await Assert.ThrowsAsync<DivideByZeroException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioExplodingCommand())
                .ExpectHandled());
    }

    /// <summary>Pipeline behaviors run between authorization and guards, and count as proceeding past authorization.</summary>
    [Fact]
    public async Task ShouldTreatBehaviorsAsProceedingPastAuthorization()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);

        await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand())
            .ExpectAuthorized()
            .ExpectGuardFailed<SlugGuard>();
        Assert.Equal(["behavior", "guard"], provider.GetRequiredService<ScenarioLog>().Steps);
    }

    /// <summary>A scenario needs a Portia composition to find the request lifecycle.</summary>
    [Fact]
    public async Task ShouldRequireRequestRegistry()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RequestScenario.For(provider).When(new ScenarioCommand()).ExpectHandled());

        Assert.Contains(nameof(RequestRegistry), error.Message, StringComparison.Ordinal);
    }

    /// <summary>A streamed request's lifecycle and items are assertable.</summary>
    [Fact]
    public async Task ShouldRunStreamedRequestLifecycle()
    {
        await using var provider = Provider();

        var items = await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioStream())
            .ExpectAuthorized()
            .ExpectGuardPassed<SlugGuard>()
            .ExpectHandled()
            .ExpectItems(1, 2);

        Assert.Equal([1, 2], items);
    }

    /// <summary>A streamed request stopped by a guard reports the guard failure and no items.</summary>
    [Fact]
    public async Task ShouldReportStreamGuardFailure()
    {
        await using var provider = Provider(RequestErrorKind.Conflict);

        var items = await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioStream())
            .ExpectGuardFailed<SlugGuard>(RequestErrorKind.Conflict)
            .ExpectNotHandled()
            .ExpectFailure(RequestErrorKind.Conflict);

        Assert.Empty(items);
    }

    /// <summary>A concurrency conflict is the settled Conflict outcome the bus reports, so it is assertable.</summary>
    [Fact]
    public async Task ShouldReportConcurrencyConflictAsConflictFailure()
    {
        await using var provider = Provider();

        await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioConflictCommand())
            .ExpectHandled()
            .ExpectFailure(RequestErrorKind.Conflict);
    }

    /// <summary>
    ///     A denial raised inside a stream handler, such as by a nested dispatch, is a fault the bus does not
    ///     settle, so it propagates instead of passing as the request's own denial.
    /// </summary>
    [Fact]
    public async Task ShouldPropagateDenialRaisedInsideStreamHandler()
    {
        await using var provider = Provider();

        _ = await Assert.ThrowsAsync<RequestAuthorizationException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioNestedDenialStream())
                .ExpectFailure(RequestErrorKind.Forbidden));
    }

    /// <summary>Discarded immutable assertions are not executed; retaining them makes the failure observable.</summary>
    [Fact]
    public async Task ShouldRequireObservationOfTheReturnedAssertion()
    {
        await using var provider = Provider();
        var log = provider.GetRequiredService<ScenarioLog>();
        var kept = RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCommand());
#pragma warning disable PORTIA107 // Deliberately reproduce the test pitfall the analyzer diagnoses.
        _ = kept.ExpectDenied();
#pragma warning restore PORTIA107
        Assert.Equal(0, log.Handled);
        await kept;
        Assert.Equal(1, log.Handled);
        var corrected = kept.ExpectDenied();
        await Assert.ThrowsAsync<ScenarioExpectationException>(async () => await corrected);
        await Assert.ThrowsAsync<ScenarioExpectationException>(async () => await corrected);
        Assert.Equal(2, log.Handled);
    }

    /// <summary>A finite scenario stops without requesting another item and disposes both owners.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    public async Task ShouldBoundStreamAndDisposeScope(int limit, int count)
    {
        await using var provider = Provider();
        var log = provider.GetRequiredService<ScenarioLog>();
        var expectations = RequestScenario.For(provider).GivenActor(Member)
            .When(new ScenarioStream(), limit).ExpectSuccess().ExpectHandled();
        var items = await expectations;
        Assert.Equal(Enumerable.Range(1, count), items);
        Assert.Equal(items, await expectations);
        Assert.Equal(count, log.StreamProduced);
        Assert.Equal(1, log.StreamDisposed);
        Assert.True(Assert.Single(log.Scopes).Disposed);
    }

    /// <summary>Invalid limits are rejected before a scenario can dispatch.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectInvalidStreamLimits(int limit)
    {
        using var provider = Provider();
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestScenario.For(provider).When(new ScenarioStream(), limit));
        Assert.Empty(provider.GetRequiredService<ScenarioLog>().Scopes);
    }

    /// <summary>Cancellation between items unwinds the enumerator and asynchronous service scope.</summary>
    [Fact]
    public async Task ShouldCancelStreamAndDisposeScope()
    {
        await using var provider = Provider();
        using var cancellation = new CancellationTokenSource();
        var log = provider.GetRequiredService<ScenarioLog>();
        log.CancelStream = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await RequestScenario.For(provider).GivenActor(Member).When(new ScenarioStream(), cancellation.Token));
        Assert.Equal(1, log.StreamDisposed);
        Assert.True(Assert.Single(log.Scopes).Disposed);
    }

    /// <summary>Caller cancellation reaches unary lifecycle components and asynchronous scope disposal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCancelUnaryRequestAndDisposeScope(bool query)
    {
        await using var provider = Provider();
        using var cancellation = new CancellationTokenSource();
        var log = provider.GetRequiredService<ScenarioLog>();
        Task operation = query
            ? RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCancelledQuery(), cancellation.Token).AsTask()
            : RequestScenario.For(provider).GivenActor(Member).When(new ScenarioCancelledCommand(), cancellation.Token).AsTask();
        await log.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(Assert.Single(log.Scopes).Disposed);
        Assert.Equal(query ? 3 : 4, log.Tokens.Count);
        Assert.All(log.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    static ServiceProvider Provider(RequestErrorKind? guardFailure = null, IPermissionEvaluator? permissions = null)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(new ScenarioLog { GuardFailure = guardFailure });
        _ = services.AddSingleton(permissions ?? TestPermissionEvaluator.AllowAll());
        _ = services.AddScoped<ScopeMarker>();
        _ = services.AddSingleton<RequestRegistry>();
        _ = services.AddScoped<ScenarioCommandHandler>();
        _ = services.AddScoped<ScenarioOpenCommandHandler>();
        _ = services.AddScoped<ScenarioPermissionCommandHandler>();
        _ = services.AddScoped<ScenarioQueryHandler>();
        _ = services.AddScoped<ScenarioStreamHandler>();
        _ = services.AddScoped<CancelledHandler>();
        _ = services.AddScoped<CancelledBehavior>();
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(new RequestPipelineBehaviorRegistration<ScenarioCancelledCommand, CancelledBehavior>(0));
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<ScenarioCancelledCommand, CancelledHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<ScenarioCancelledQuery, CancelledHandler, int>());
        _ = services.AddScoped<ScenarioExplodingCommandHandler>();
        _ = services.AddScoped<ScenarioConflictCommandHandler>();
        _ = services.AddScoped<ScenarioNestedDenialStreamHandler>();
        _ = services.AddScoped<ScenarioAuthorizer>();
        _ = services.AddScoped<SlugGuard>();
        _ = services.AddScoped<OpenCommandGuard>();
        _ = services.AddScoped<ScenarioBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioCommand, ScenarioCommandHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioOpenCommand, ScenarioOpenCommandHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioPermissionCommand, ScenarioPermissionCommandHandler>(_ =>
                "scenario:permission"));
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioQuery, ScenarioQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<ScenarioStream, ScenarioStreamHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioExplodingCommand, ScenarioExplodingCommandHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<ScenarioConflictCommand, ScenarioConflictCommandHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<ScenarioNestedDenialStream, ScenarioNestedDenialStreamHandler, int>());
        _ = services.AddSingleton<RequestAuthorizerRegistration>(
            new RequestAuthorizerRegistration<IScenarioFamily, ScenarioAuthorizer>());
        _ = services.AddSingleton<RequestGuardRegistration>(new RequestGuardRegistration<IScenarioFamily, SlugGuard>());
        _ = services.AddSingleton<RequestGuardRegistration>(
            new RequestGuardRegistration<ScenarioOpenCommand, OpenCommandGuard>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<ScenarioCommand, ScenarioBehavior>(0));
        return services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateScopes = true, ValidateOnBuild = true });
    }

    internal sealed class ScenarioLog
    {
        public RequestErrorKind? GuardFailure { get; init; }
        public int Handled { get; set; }
        public int StreamDisposed { get; set; }
        public int StreamProduced { get; set; }
        public List<CancellationToken> Tokens { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource? CancelStream { get; set; }
        public List<string> Steps { get; } = [];
        public List<ScopeMarker> Scopes { get; } = [];
    }

    internal sealed class ScopeMarker : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal interface IScenarioFamily : IRequestBase;

    internal sealed record ScenarioCommand : IRequest, IScenarioFamily;

    internal sealed record ScenarioOpenCommand : IRequest;

    internal sealed record ScenarioPermissionCommand : IRequest;

    internal sealed record ScenarioQuery : IRequest<int>, IScenarioFamily;

    internal sealed record ScenarioCancelledCommand : IRequest, IScenarioFamily;
    internal sealed record ScenarioCancelledQuery : IRequest<int>, IScenarioFamily;

    internal sealed class CancelledBehavior(ScenarioLog log) : IRequestPipelineBehavior<ScenarioCancelledCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioCancelledCommand> context, RequestPipelineNext next, CancellationToken ct)
        {
            log.Tokens.Add(ct);
            return next(ct);
        }
    }

    internal sealed class CancelledHandler(ScenarioLog log, ScopeMarker scope)
        : IRequestHandler<ScenarioCancelledCommand>, IRequestHandler<ScenarioCancelledQuery, int>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<ScenarioCancelledCommand> context, CancellationToken ct)
        {
            await WaitAsync(ct);
            return Result.Success;
        }

        public async ValueTask<Result<int>> HandleAsync(IRequestContext<ScenarioCancelledQuery> context, CancellationToken ct)
        {
            await WaitAsync(ct);
            return Result<int>.Success(1);
        }

        async Task WaitAsync(CancellationToken ct)
        {
            log.Scopes.Add(scope);
            log.Tokens.Add(ct);
            log.Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    internal sealed record ScenarioStream : IStreamRequest<int>, IScenarioFamily;

    internal sealed record ScenarioExplodingCommand : IRequest;

    internal sealed record ScenarioConflictCommand : IRequest, IScenarioFamily;

    internal sealed record ScenarioNestedDenialStream : IStreamRequest<int>, IScenarioFamily;

    internal sealed class ScenarioConflictCommandHandler : IRequestHandler<ScenarioConflictCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioConflictCommand> context, CancellationToken ct) =>
            throw new EventStreamConcurrencyException("stale");
    }

    internal sealed class ScenarioNestedDenialStreamHandler : IStreamRequestHandler<ScenarioNestedDenialStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<ScenarioNestedDenialStream> context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return 1;
            await Task.Yield();
            throw new RequestAuthorizationException(new RequestError(RequestErrorKind.Forbidden, "nested"));
        }
    }

    internal sealed class ScenarioCommandHandler(ScenarioLog log, ScopeMarker scope) : IRequestHandler<ScenarioCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioCommand> context, CancellationToken ct)
        {
            log.Handled++;
            log.Steps.Add("handler");
            log.Scopes.Add(scope);
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class ScenarioOpenCommandHandler : IRequestHandler<ScenarioOpenCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioOpenCommand> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal sealed class ScenarioPermissionCommandHandler : IRequestHandler<ScenarioPermissionCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioPermissionCommand> context,
            CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal sealed class ScenarioQueryHandler : IRequestHandler<ScenarioQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<ScenarioQuery> context, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(42));
    }

    internal sealed class ScenarioStreamHandler(ScenarioLog log, ScopeMarker scope) : IStreamRequestHandler<ScenarioStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<ScenarioStream> context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            log.Scopes.Add(scope);
            try
            {
                log.StreamProduced++;
                yield return 1;
                log.CancelStream?.Cancel();
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                log.StreamProduced++;
                yield return 2;
            }
            finally
            {
                log.StreamDisposed++;
            }
        }
    }

    internal sealed class ScenarioExplodingCommandHandler : IRequestHandler<ScenarioExplodingCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioExplodingCommand> context, CancellationToken ct) =>
            throw new DivideByZeroException();
    }

    internal sealed class ScenarioAuthorizer(ScenarioLog log) : IRequestAuthorizer<IScenarioFamily>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<IScenarioFamily> context, CancellationToken ct)
        {
            log.Tokens.Add(ct);
            return ValueTask.FromResult(context.Actor.Identity?.IsAuthenticated == true
                ? Result.Success
                : Result.Failure(new RequestError(RequestErrorKind.Forbidden, "Members only.")));
        }
    }

    internal sealed class SlugGuard(ScenarioLog log) : IRequestGuard<IScenarioFamily>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<IScenarioFamily> context, CancellationToken ct)
        {
            log.Tokens.Add(ct);
            log.Steps.Add("guard");
            return ValueTask.FromResult(log.GuardFailure is { } kind
                ? Result.Failure(new RequestError(kind, "The slug is taken."))
                : Result.Success);
        }
    }

    internal sealed class OpenCommandGuard : IRequestGuard<ScenarioOpenCommand>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<ScenarioOpenCommand> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal sealed class ScenarioBehavior(ScenarioLog log) : IRequestPipelineBehavior<ScenarioCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<ScenarioCommand> context, RequestPipelineNext next,
            CancellationToken ct)
        {
            log.Steps.Add("behavior");
            return next(ct);
        }
    }
}
