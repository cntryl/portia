using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Verifies ordered typed request pipeline behavior composition.</summary>
public sealed partial class RequestPipelineBehaviorTests
{
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

    /// <summary>Authorization denial prevents all behavior execution.</summary>
    [Fact]
    public async Task ShouldAuthorizeBeforeEnteringBehaviorChain()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<PipelineAction, PipelineActionHandler>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(new RequestPipelineBehaviorRegistration<IBehaviorRequest, OuterBehavior>(0));
        _ = services.AddSingleton<RequestAuthorizerRegistration>(new RequestAuthorizerRegistration<PipelineAction, DenyingAuthorizer>());
        _ = services.AddSingleton<DenyingAuthorizer>();
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IRequestBus>().SendAsync(new PipelineAction(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Empty(calls);
    }

    /// <summary>Result-bearing and streaming behaviors compose around their handlers.</summary>
    [Fact]
    public async Task ShouldComposeResultAndStreamingBehaviors()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<PipelineQuery, PipelineQueryHandler, int>());
        _ = services.AddSingleton<RequestHandlerRegistration>(new StreamRequestRegistration<PipelineStream, PipelineStreamHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(new RequestPipelineBehaviorRegistration<PipelineQuery, QueryBehavior, int>(0));
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(new StreamRequestPipelineBehaviorRegistration<PipelineStream, StreamBehavior, int>(0));
        using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRequestBus>();

        var result = await bus.SendAsync(new PipelineQuery(), RequestActor.System);
        var items = new List<int>();
        await foreach (var item in bus.StreamAsync(new PipelineStream(), RequestActor.System)) items.Add(item);

        Assert.Equal(42, result.Value);
        Assert.Equal([0, 1, 2, 3], items);
    }

    /// <summary>Invalid results identify the framework extension that returned them.</summary>
    [Fact]
    public async Task ShouldNameOwnerOfUninitializedResult()
    {
        using var provider = Provider([], new RequestRegistration<InvalidPipelineAction, InvalidPipelineHandler>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.GetRequiredService<IRequestBus>().SendAsync(new InvalidPipelineAction(), RequestActor.System));

        Assert.Contains(typeof(InvalidPipelineHandler).FullName!, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A behavior selected by scope but unable to serve the dispatched request's shape is
    /// skipped. Scope matching is assignability only, so a request implementing both a no-result
    /// family interface and <see cref="IRequest{TOut}" /> reaches a no-result behavior through a
    /// result-bearing dispatch it cannot handle.
    /// </summary>
    [Fact]
    public async Task ShouldSkipBehaviorsThatCannotServeTheDispatchedShape()
    {
        var calls = new List<string>();
        var services = Services(calls);
        _ = services.AddSingleton<MixedShapeHandler>();
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<MixedShape, MixedShapeHandler, int>());
        _ = services.AddSingleton<RequestPipelineBehaviorRegistration>(new RequestPipelineBehaviorRegistration<IBehaviorRequest, OuterBehavior>(0));
        using var provider = services.BuildServiceProvider();

        var bus = provider.GetRequiredService<IRequestBus>();
        var result = await bus.DispatchAsync<int>(new MixedShape(), bus.CreateContext(RequestActor.System));

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        Assert.Empty(calls);
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
    }

    static ServiceProvider Provider(List<string> calls, RequestHandlerRegistration handler,
        params RequestPipelineBehaviorRegistration[] behaviors)
    {
        var services = Services(calls);
        _ = services.AddSingleton(handler);
        foreach (var behavior in behaviors) _ = services.AddSingleton(behavior);
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
    internal sealed record CustomInvocation : RequestInvocation { public override string TransportName => "custom"; }

    internal sealed class PipelineActionHandler(List<string> calls) : IRequestHandler<PipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context, CancellationToken ct)
        { calls.Add("handler"); return ValueTask.FromResult(Result.Success); }
    }

    internal sealed class PipelineQueryHandler : IRequestHandler<PipelineQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context, CancellationToken ct)
            => ValueTask.FromResult(Result<int>.Success(41));
    }

    internal sealed class PipelineStreamHandler : IStreamRequestHandler<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context, [EnumeratorCancellation] CancellationToken ct)
        { yield return 1; await Task.Yield(); yield return 2; }
    }

    internal sealed class MixedShapeHandler : IRequestHandler<MixedShape, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<MixedShape> context, CancellationToken ct)
            => ValueTask.FromResult(Result<int>.Success(7));
    }

    internal sealed class InvalidPipelineHandler : IRequestHandler<InvalidPipelineAction>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<InvalidPipelineAction> context, CancellationToken ct) => default;
    }

    internal sealed class OuterBehavior(List<string> calls) : IRequestPipelineBehavior<IBehaviorRequest>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<IBehaviorRequest> context, RequestHandler nextHandler, CancellationToken ct)
        { calls.Add("outer-before"); var result = await nextHandler(ct); calls.Add("outer-after"); return result; }
    }

    internal sealed class InnerBehavior(List<string> calls) : IRequestPipelineBehavior<PipelineAction>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<PipelineAction> context, RequestHandler nextHandler, CancellationToken ct)
        { calls.Add("inner-before"); var result = await nextHandler(ct); calls.Add("inner-after"); return result; }
    }

    internal sealed class QueryBehavior : IRequestPipelineBehavior<PipelineQuery, int>
    {
        public async ValueTask<Result<int>> HandleAsync(IRequestContext<PipelineQuery> context, RequestHandler<int> nextHandler, CancellationToken ct)
        { var result = await nextHandler(ct); return Result<int>.Success(result.Value + 1); }
    }

    internal sealed class StreamBehavior : IStreamRequestPipelineBehavior<PipelineStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<PipelineStream> context, StreamRequestHandler<int> nextHandler,
            [EnumeratorCancellation] CancellationToken ct)
        { yield return 0; await foreach (var item in nextHandler(ct).WithCancellation(ct)) yield return item; yield return 3; }
    }

    internal sealed class DenyingAuthorizer : IRequestAuthorizer<PipelineAction>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<PipelineAction> context, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default)
            => ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Forbidden, "denied")));
    }
}
