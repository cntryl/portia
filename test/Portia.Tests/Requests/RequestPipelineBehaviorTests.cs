using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Verifies ordered typed request pipeline behavior composition.</summary>
public sealed class RequestPipelineBehaviorTests
{
    const string SingleUseMessage =
        "A request pipeline continuation may be invoked at most once and only during its behavior invocation.";

    /// <summary>Request-family and concrete behaviors wrap the handler in stable order.</summary>
    [Fact]
    public async Task ShouldRunMatchingBehaviorsInOrderAroundHandler()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            new RequestRegistration<PipelineAction, PipelineActionHandler>(),
            new RequestPipelineBehaviorRegistration<IBehaviorRequest, OuterBehavior>(-10),
            new RequestPipelineBehaviorRegistration<PipelineAction, InnerBehavior>(20));

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["outer-before", "inner-before", "handler", "inner-after", "outer-after"], calls);
    }

    /// <summary>The extended-state boundary enforces use-after-close and use-after-completion.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public void ShouldEnforceSingleUseAtExtendedStateBoundary(int position)
    {
        var closed = new RequestPipelineFrame<PipelineAction>(
            new object(), null!, null!, new PipelineAction(), null!, 32);
        closed.Use(position);
        closed.Close(position);

        var closedFailure = Assert.Throws<InvalidOperationException>(() => closed.Use(position));

        var completed = new RequestPipelineFrame<PipelineAction>(
            new object(), null!, null!, new PipelineAction(), null!, 32);
        completed.Complete();
        var completedFailure = Assert.Throws<InvalidOperationException>(() => completed.Use(position));
        Assert.Equal(SingleUseMessage, closedFailure.Message);
        Assert.Equal(SingleUseMessage, completedFailure.Message);
    }

    /// <summary>Authorization denial prevents all behavior execution.</summary>
    [Fact]
    public async Task ShouldAuthorizeBeforeEnteringBehaviorChain()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<IBehaviorRequest, OuterBehavior>(0));
        _ = services.AddSingleton<RequestAuthorizerRegistration>(
            new RequestAuthorizerRegistration<PipelineAction, DenyingAuthorizer>());
        _ = services.AddSingleton<DenyingAuthorizer>();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Empty(calls);
    }

    /// <summary>Result-bearing and streaming behaviors compose around their handlers.</summary>
    [Fact]
    public async Task ShouldComposeResultAndStreamingBehaviors()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineQuery, QueryBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new StreamRequestPipelineBehaviorRegistration<PipelineStream, StreamBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        var result = await bus.SendAsync(new PipelineQuery(), RequestActor.System);
        var items = new List<int>();
        await foreach (var item in bus.StreamAsync(new PipelineStream(), RequestActor.System))
            items.Add(item);

        Assert.Equal(42, result.Value);
        Assert.Equal([0, 1, 2, 3], items);
    }

    /// <summary>Invalid results identify the framework extension that returned them.</summary>
    [Fact]
    public async Task ShouldNameOwnerOfUninitializedResult()
    {
        using var provider = Provider([], new RequestRegistration<InvalidPipelineAction, InvalidPipelineHandler>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IRequestBus>()
                .SendAsync(new InvalidPipelineAction(), RequestActor.System));

        var handlerName = typeof(InvalidPipelineHandler).FullName;
        Assert.NotNull(handlerName);
        Assert.Contains(handlerName, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A behavior selected by scope but unable to serve the dispatched request's shape is
    ///     skipped. Scope matching is assignability only, so a request implementing both a no-result
    ///     family interface and <see cref="IRequest{TOut}" /> reaches a no-result behavior through a
    ///     result-bearing dispatch it cannot handle.
    /// </summary>
    [Fact]
    public async Task ShouldSkipBehaviorsThatCannotServeTheDispatchedShape()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<MixedShapeHandler>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<MixedShape, MixedShapeHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<IBehaviorRequest, OuterBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var bus = provider.GetRequiredService<IRequestBus>();
        var result = await bus.DispatchAsync<int>(new MixedShape(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        Assert.Empty(calls);
    }

    /// <summary>A no-result behavior may short-circuit without invoking downstream work.</summary>
    [Fact]
    public async Task ShouldAllowBehaviorToSkipContinuation()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<ShortCircuitBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, ShortCircuitBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["short-circuit"], calls);
    }

    /// <summary>Result-bearing and streaming behaviors may also short-circuit their continuations.</summary>
    [Fact]
    public async Task ShouldAllowResultAndStreamingBehaviorsToSkipContinuation()
    {
        var services = Services([]);
        _ = services.AddSingleton<ShortCircuitQueryBehavior>();
        _ = services.AddSingleton<ShortCircuitStreamBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineQuery, ShortCircuitQueryBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new StreamRequestPipelineBehaviorRegistration<PipelineStream, ShortCircuitStreamBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        var query = await bus.SendAsync(new PipelineQuery(), RequestActor.System);
        var stream = new List<int>();
        await foreach (var item in bus.StreamAsync(new PipelineStream(), RequestActor.System))
            stream.Add(item);

        Assert.True(query.IsSuccess);
        Assert.Equal(99, query.Value);
        Assert.Equal([9], stream);
    }

    /// <summary>A continuation is single-use and cannot execute a handler twice.</summary>
    [Fact]
    public async Task ShouldRejectSecondSequentialContinuationInvocation()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<TwiceBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, TwiceBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IRequestBus>().SendAsync(new PipelineAction(), RequestActor.System));

        Assert.Equal(SingleUseMessage, failure.Message);
        Assert.Equal(["handler"], calls);
    }

    /// <summary>Two concurrent continuation calls admit one winner and reject the other.</summary>
    [Fact]
    public async Task ShouldAllowExactlyOneConcurrentContinuationInvocation()
    {
        var calls = new List<string>();
        var behavior = new ConcurrentBehavior();
        var services = Services(calls);
        _ = services.AddSingleton(behavior);
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, ConcurrentBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["handler"], calls);
        Assert.Equal(SingleUseMessage, Assert.Single(behavior.Failures).Message);
    }

    /// <summary>Awaiting before the first continuation call is valid.</summary>
    [Fact]
    public async Task ShouldAllowAwaitBeforeFirstContinuationInvocation()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<YieldBeforeContinuationBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, YieldBeforeContinuationBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["handler"], calls);
    }

    /// <summary>An inner continuation expires when its own behavior returns, even while an outer behavior remains.</summary>
    [Fact]
    public async Task ShouldRejectInnerContinuationAfterInnerBehaviorReturns()
    {
        var calls = new List<string>();
        var retained = new RetainingBehavior();
        var services = Services(calls);
        _ = services.AddSingleton(retained);
        _ = services.AddSingleton<InvokeRetainedInnerBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, InvokeRetainedInnerBehavior>(-10));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, RetainingBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IRequestBus>().SendAsync(new PipelineAction(), RequestActor.System));

        Assert.Equal(SingleUseMessage, failure.Message);
        Assert.Empty(calls);
    }

    /// <summary>A continuation retained by a behavior expires when that behavior returns.</summary>
    [Fact]
    public async Task ShouldRejectContinuationRetainedAfterBehaviorReturns()
    {
        var calls = new List<string>();
        var behavior = new RetainingBehavior();
        var services = Services(calls);
        _ = services.AddSingleton(behavior);
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, RetainingBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>()
            .SendAsync(new PipelineAction(), RequestActor.System);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Continuation!(default));

        Assert.True(result.IsSuccess);
        Assert.Equal(SingleUseMessage, failure.Message);
        Assert.Empty(calls);
    }

    /// <summary>Result-bearing and streaming continuations enforce the same single-use contract.</summary>
    [Fact]
    public async Task ShouldRejectSecondResultAndStreamingContinuationInvocations()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<TwiceQueryBehavior>();
        _ = services.AddSingleton<TwiceStreamBehavior>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineQuery, TwiceQueryBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new StreamRequestPipelineBehaviorRegistration<PipelineStream, TwiceStreamBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        var queryFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await bus.SendAsync(new PipelineQuery(), RequestActor.System));
        var streamFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in bus.StreamAsync(new PipelineStream(), RequestActor.System))
            {
            }
        });

        Assert.Equal(SingleUseMessage, queryFailure.Message);
        Assert.Equal(SingleUseMessage, streamFailure.Message);
    }

    /// <summary>Unused and used continuations cannot adopt later unary, result, or stream invocations.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ShouldRejectExpiredContinuationsInsideLaterInvocations(int shape, bool useFirst)
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton(new StaleContinuationBehavior(useFirst));
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, StaleContinuationBehavior>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineQuery, StaleContinuationBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new StreamRequestPipelineBehaviorRegistration<PipelineStream, StaleContinuationBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        async Task DispatchAsync()
        {
            if (shape == 0)
                _ = await bus.SendAsync(new PipelineAction(), RequestActor.System);
            else if (shape == 1)
                _ = await bus.SendAsync(new PipelineQuery(), RequestActor.System);
            else
                await foreach (var _ in bus.StreamAsync(new PipelineStream(), RequestActor.System))
                    break; // Dispose a live stream as well as exercising natural short-circuit completion.
        }

        await DispatchAsync();
        var initialCalls = calls.Count;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(DispatchAsync);
        Assert.Equal(SingleUseMessage, failure.Message);
        Assert.Equal(initialCalls, calls.Count);
    }

    /// <summary>A live continuation cannot be borrowed by a nested or concurrent invocation.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ShouldRejectContinuationBorrowedFromActiveInvocation(int shape, bool nested)
    {
        var behavior = new BorrowingContinuationBehavior();
        var services = Services([]);
        _ = services.AddSingleton(behavior);
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineAction, BorrowingContinuationBehavior>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<PipelineQuery, BorrowingContinuationBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new StreamRequestPipelineBehaviorRegistration<PipelineStream, BorrowingContinuationBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task DispatchAsync()
        {
            if (shape == 0)
                Assert.True((await bus.SendAsync(new PipelineAction(), RequestActor.System, timeout.Token)).IsSuccess);
            else if (shape == 1)
                Assert.Equal(41, (await bus.SendAsync(new PipelineQuery(), RequestActor.System, timeout.Token)).Value);
            else
            {
                var items = new List<int>();
                await foreach (var item in bus.StreamAsync(new PipelineStream(), RequestActor.System, timeout.Token))
                    items.Add(item);
                Assert.Equal([1, 2], items);
            }
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behavior.OnFirst = nested ? DispatchAsync : () => release.Task.WaitAsync(timeout.Token);
        var first = DispatchAsync();
        if (!nested)
        {
            try
            {
                await behavior.Ready.Task.WaitAsync(timeout.Token);
                await DispatchAsync();
            }
            finally
            {
                release.TrySetResult();
            }
        }
        await first;
        Assert.Equal(SingleUseMessage, Assert.Single(behavior.Failures).Message);
    }

    /// <summary>The inline and extended position states both admit one concurrent winner.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public async Task ShouldAllowOneConcurrentWinnerAtStateBoundary(int position)
    {
        var frame = new RequestPipelineFrame<PipelineAction>(
            new object(), null!, null!, new PipelineAction(), null!, 32);
        using var ready = new Barrier(2);
        Task<bool> Attempt() => Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(10)));
            try
            {
                frame.Use(position);
                return true;
            }
            catch (InvalidOperationException failure)
            {
                Assert.Equal(SingleUseMessage, failure.Message);
                return false;
            }
        });
        Assert.Equal(1, (await Task.WhenAll(Attempt(), Attempt())).Count(won => won));
    }

    /// <summary>The concrete typed context instance is shared through a family behavior and handler.</summary>
    [Fact]
    public async Task ShouldShareOneConcreteRequestContextAcrossPipeline()
    {
        var seen = new List<IRequestContext>();
        var services = Services([]);
        _ = services.AddSingleton(seen);
        _ = services.AddSingleton<ContextBehavior>();
        _ = services.AddSingleton<ContextHandler>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<PipelineAction, ContextHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<IBehaviorRequest, ContextBehavior>(0));
        using var provider = services.BuildServiceProvider();

        _ = await provider.GetRequiredService<IRequestBus>().SendAsync(new PipelineAction(), RequestActor.System);

        Assert.Equal(2, seen.Count);
        Assert.Same(seen[0], seen[1]);
        Assert.IsType<RequestContext<PipelineAction>>(seen[0]);
    }

    /// <summary>Nested and concurrent dispatches through one scoped bus retain distinct execution frames.</summary>
    [Fact]
    public async Task ShouldIsolateNestedAndConcurrentDispatchFrames()
    {
        var services = Services([]);
        var handled = new ConcurrentBag<int>();
        var gate = new DispatchGate();
        _ = services.AddSingleton(handled);
        _ = services.AddSingleton(gate);
        _ = services.AddScoped<NestedConcurrentBehavior>();
        _ = services.AddSingleton<NestedConcurrentHandler>();
        _ = services.AddSingleton<RequestHandlerRegistration>(
            new RequestRegistration<NestedConcurrentAction, NestedConcurrentHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(
            new RequestPipelineBehaviorRegistration<NestedConcurrentAction, NestedConcurrentBehavior>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        var results = await Task.WhenAll(
            bus.SendAsync(new NestedConcurrentAction(1, true), RequestActor.System).AsTask(),
            bus.SendAsync(new NestedConcurrentAction(2, true), RequestActor.System).AsTask());
        Assert.All(results, result => Assert.True(result.IsSuccess));

        gate.Bypass = true;
        var nested = await bus.SendAsync(new NestedConcurrentAction(3, false), RequestActor.System);
        Assert.True(nested.IsSuccess);
        Assert.Equal([1, 2, 3, 103], handled.Order());
    }

    /// <summary>Built-in and custom invocations expose bounded transport names.</summary>
    [Fact]
    public void ShouldExposeStableBuiltInAndCustomTransportNames()
    {
        Assert.Equal("local", new DirectInvocation().TransportName);
        Assert.Equal("http", new HttpInvocation("GET", "/x", "/x", "x").TransportName);
        Assert.Equal("rpc", new RpcInvocation("x").TransportName);
        Assert.Equal("queue", new QueueInvocation("x", 1).TransportName);
        Assert.Equal("notice", new NoticeInvocation("x").TransportName);
        Assert.Equal("schedule", new ScheduleInvocation("x").TransportName);
        Assert.Equal("custom", new CustomInvocation().TransportName);
        Assert.Equal(RequestTraceRelationship.Parent, new DirectInvocation().TraceRelationship);
        Assert.Equal(RequestTraceRelationship.Parent, new HttpInvocation("GET", "/x", "/x", "x").TraceRelationship);
        Assert.Equal(RequestTraceRelationship.Parent, new RpcInvocation("x").TraceRelationship);
        Assert.Equal(RequestTraceRelationship.Link, new QueueInvocation("x", 1).TraceRelationship);
        Assert.Equal(RequestTraceRelationship.Link, new NoticeInvocation("x").TraceRelationship);
        Assert.Equal(RequestTraceRelationship.Link, new ScheduleInvocation("x").TraceRelationship);
        Assert.Null(new DirectInvocation().MessagingSystem);
    }

    static ServiceProvider Provider(List<string> calls, RequestHandlerRegistration handler,
        params RequestPipelineBehaviorRegistration[] behaviors)
    {
        var services = Services(calls);
        _ = services.AddSingleton(handler);
        foreach (var behavior in behaviors)
            _ = services.AddSingleton(behavior);
        return services.BuildServiceProvider();
    }

    static ServiceCollection Services(List<string> calls)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton(calls);
        _ = services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = services.AddSingleton<PipelineActionHandler>();
        _ = services.AddSingleton<PipelineQueryHandler>();
        _ = services.AddSingleton<PipelineStreamHandler>();
        _ = services.AddSingleton<InvalidPipelineHandler>();
        _ = services.AddSingleton<OuterBehavior>();
        _ = services.AddSingleton<InnerBehavior>();
        _ = services.AddSingleton<QueryBehavior>();
        _ = services.AddSingleton<StreamBehavior>();
        _ = services.AddSingleton<RequestRegistry>();
        _ = services.AddScoped<IRequestBus, RequestBus>();
        return services;
    }

    internal interface IBehaviorRequest : IRequest;

    internal sealed record PipelineAction : IBehaviorRequest;

    internal sealed record PipelineQuery : IRequest<int>;

    internal sealed record PipelineStream : IStreamRequest<int>;

    internal sealed record InvalidPipelineAction : IRequest;

    internal sealed record MixedShape : IRequest<int>, IBehaviorRequest;

    internal sealed record NestedConcurrentAction(int Id, bool Inner) : IRequest;

    internal sealed record CustomInvocation : RequestInvocation
    {
        public override string TransportName => "custom";

        public override RequestTraceRelationship TraceRelationship => RequestTraceRelationship.Parent;
    }

    internal sealed class PipelineActionHandler(List<string> calls) : IRequestHandler<PipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context, CancellationToken ct)
        {
            calls.Add("handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class PipelineQueryHandler : IRequestHandler<PipelineQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context, CancellationToken ct)
            => ValueTask.FromResult(Result<int>.Success(41));
    }

    internal sealed class PipelineStreamHandler : IStreamRequestHandler<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return 1;
            await Task.Yield();
            yield return 2;
        }
    }

    internal sealed class MixedShapeHandler : IRequestHandler<MixedShape, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<MixedShape> context, CancellationToken ct)
            => ValueTask.FromResult(Result<int>.Success(7));
    }

    internal sealed class InvalidPipelineHandler : IRequestHandler<InvalidPipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidPipelineAction> context, CancellationToken ct) =>
            default;
    }

    internal sealed class OuterBehavior(List<string> calls) : IRequestPipelineBehavior<IBehaviorRequest>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<IBehaviorRequest> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            calls.Add("outer-before");
            var result = await continuation(ct);
            calls.Add("outer-after");
            return result;
        }
    }

    internal sealed class InnerBehavior(List<string> calls) : IRequestPipelineBehavior<PipelineAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            calls.Add("inner-before");
            var result = await continuation(ct);
            calls.Add("inner-after");
            return result;
        }
    }

    internal sealed class QueryBehavior : IRequestPipelineBehavior<PipelineQuery, int>
    {
        public async ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context,
            RequestPipelineNext<int> continuation, CancellationToken ct)
        {
            var result = await continuation(ct);
            return Result<int>.Success(result.Value + 1);
        }
    }

    internal sealed class StreamBehavior : IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            StreamRequestPipelineNext<int> continuation,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return 0;
            await foreach (var item in continuation(ct).WithCancellation(ct))
                yield return item;
            yield return 3;
        }
    }

    internal sealed class DenyingAuthorizer : IRequestAuthorizer<PipelineAction>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<PipelineAction> context, CancellationToken ct)
            => ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Forbidden, "denied")));
    }

    internal sealed class ShortCircuitBehavior(List<string> calls) : IRequestPipelineBehavior<PipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            calls.Add("short-circuit");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class ShortCircuitQueryBehavior : IRequestPipelineBehavior<PipelineQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context,
            RequestPipelineNext<int> continuation, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(99));
    }

    internal sealed class ShortCircuitStreamBehavior : IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            StreamRequestPipelineNext<int> continuation, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return 9;
            await Task.CompletedTask;
        }
    }

    internal sealed class TwiceBehavior : IRequestPipelineBehavior<PipelineAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            _ = await continuation(ct);
            return await continuation(ct);
        }
    }

    internal sealed class RetainingBehavior : IRequestPipelineBehavior<PipelineAction>
    {
        public RequestPipelineNext? Continuation { get; private set; }

        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            Continuation = continuation;
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class ConcurrentBehavior : IRequestPipelineBehavior<PipelineAction>
    {
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _ready;

        public List<InvalidOperationException> Failures { get; } = [];

        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            async Task<Result?> AttemptAsync()
            {
                if (Interlocked.Increment(ref _ready) == 2)
                    _ = _release.TrySetResult();
                await _release.Task.WaitAsync(ct);
                try
                {
                    return await continuation(ct);
                }
                catch (InvalidOperationException ex)
                {
                    Failures.Add(ex);
                    return null;
                }
            }

            var outcomes = await Task.WhenAll(Task.Run(AttemptAsync, ct), Task.Run(AttemptAsync, ct));
            return outcomes.Single(result => result is not null)!.Value;
        }
    }

    internal sealed class YieldBeforeContinuationBehavior : IRequestPipelineBehavior<PipelineAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            await Task.Yield();
            return await continuation(ct);
        }
    }

    internal sealed class InvokeRetainedInnerBehavior(RetainingBehavior inner)
        : IRequestPipelineBehavior<PipelineAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            _ = await continuation(ct);
            return await inner.Continuation!(ct);
        }
    }

    internal sealed class BorrowingContinuationBehavior : IRequestPipelineBehavior<PipelineAction>,
        IRequestPipelineBehavior<PipelineQuery, int>, IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        int _invocations;
        Func<CancellationToken, Task>? _borrowed;
        public Func<Task> OnFirst { get; set; } = null!;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<InvalidOperationException> Failures { get; } = [];

        async Task EnterAsync(Func<CancellationToken, Task> continuation, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                _borrowed = continuation;
                Ready.TrySetResult();
                await OnFirst();
            }
            else
            {
                try
                {
                    await _borrowed!(ct);
                }
                catch (InvalidOperationException failure)
                {
                    Failures.Add(failure);
                }
            }
        }

        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            await EnterAsync(async token => { _ = await continuation(token); }, ct);
            return await continuation(ct);
        }

        public async ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context,
            RequestPipelineNext<int> continuation, CancellationToken ct)
        {
            await EnterAsync(async token => { _ = await continuation(token); }, ct);
            return await continuation(ct);
        }

        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            StreamRequestPipelineNext<int> continuation, [EnumeratorCancellation] CancellationToken ct)
        {
            await EnterAsync(async token =>
            {
                await foreach (var _ in continuation(token).WithCancellation(token))
                { }
            }, ct);
            await foreach (var item in continuation(ct).WithCancellation(ct))
                yield return item;
        }
    }

    internal sealed class StaleContinuationBehavior(bool useFirst) : IRequestPipelineBehavior<PipelineAction>,
        IRequestPipelineBehavior<PipelineQuery, int>, IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        RequestPipelineNext? _unary;
        RequestPipelineNext<int>? _result;
        StreamRequestPipelineNext<int>? _stream;

        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            if (_unary is not null)
                return _unary(ct);
            _unary = continuation;
            return useFirst ? continuation(ct) : ValueTask.FromResult(Result.Success);
        }

        public ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context,
            RequestPipelineNext<int> continuation, CancellationToken ct)
        {
            if (_result is not null)
                return _result(ct);
            _result = continuation;
            return useFirst ? continuation(ct) : ValueTask.FromResult(Result<int>.Success(0));
        }

        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            StreamRequestPipelineNext<int> continuation, [EnumeratorCancellation] CancellationToken ct)
        {
            if (_stream is null)
            {
                _stream = continuation;
                if (useFirst)
                    await foreach (var item in continuation(ct).WithCancellation(ct))
                        yield return item;
                yield break;
            }
            await foreach (var item in _stream(ct).WithCancellation(ct))
                yield return item;
        }
    }

    internal sealed class TwiceQueryBehavior : IRequestPipelineBehavior<PipelineQuery, int>
    {
        public async ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context,
            RequestPipelineNext<int> continuation, CancellationToken ct)
        {
            _ = await continuation(ct);
            return await continuation(ct);
        }
    }

    internal sealed class TwiceStreamBehavior : IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context,
            StreamRequestPipelineNext<int> continuation, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var item in continuation(ct).WithCancellation(ct))
                yield return item;
            await foreach (var item in continuation(ct).WithCancellation(ct))
                yield return item;
        }
    }

    internal sealed class ContextBehavior(List<IRequestContext> seen) : IRequestPipelineBehavior<IBehaviorRequest>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<IBehaviorRequest> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            seen.Add(context);
            return continuation(ct);
        }
    }

    internal sealed class ContextHandler(List<IRequestContext> seen) : IRequestHandler<PipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context, CancellationToken ct)
        {
            seen.Add(context);
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class DispatchGate
    {
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _arrivals;

        public bool Bypass { get; set; }

        public async Task EnterAsync(CancellationToken ct)
        {
            if (Bypass)
                return;
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ = _release.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
    }

    internal sealed class NestedConcurrentBehavior(IRequestBus bus, DispatchGate gate)
        : IRequestPipelineBehavior<NestedConcurrentAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<NestedConcurrentAction> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            await gate.EnterAsync(ct);
            if (!context.Request.Inner)
                _ = await bus.SendAsync(new NestedConcurrentAction(context.Request.Id + 100, true), context.Actor, ct);
            return await continuation(ct);
        }
    }

    internal sealed class NestedConcurrentHandler(ConcurrentBag<int> handled)
        : IRequestHandler<NestedConcurrentAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<NestedConcurrentAction> context, CancellationToken ct)
        {
            handled.Add(context.Request.Id);
            return ValueTask.FromResult(Result.Success);
        }
    }
}
