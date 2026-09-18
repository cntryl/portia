using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Verifies reusable asynchronous request guards at the unary handler boundary.</summary>
public sealed class RequestGuardTests
{
    static readonly RequestError GuardFailure =
        new(RequestErrorKind.Conflict, "The surrounding state rejects this request.");

    static readonly RequestError HandlerFailure =
        new(RequestErrorKind.Validation, "The handler rejected this request.");

    /// <summary>A successful command guard runs before its handler and shares the typed context.</summary>
    [Fact]
    public async Task ShouldRunCommandGuardBeforeHandlerWithSharedContext()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards: [new RequestGuardRegistration<GuardedCommand, RecordingCommandGuard>()]);

        var result = await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["guard", "handler"], calls);
        Assert.Same(RecordingCommandGuard.Context, CommandHandler.Context);
    }

    /// <summary>The first command guard failure is returned unchanged and skips later work.</summary>
    [Fact]
    public async Task ShouldShortCircuitCommandOnFirstGuardFailure()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards:
            [
                new RequestGuardRegistration<GuardedCommand, FailingCommandGuard>(),
                new RequestGuardRegistration<GuardedCommand, RecordingCommandGuard>()
            ]);

        var result = await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Same(GuardFailure, result.Error);
        Assert.Equal(["failing-guard"], calls);
    }

    /// <summary>A query guard failure maps the same error into a result-bearing failure.</summary>
    [Fact]
    public async Task ShouldMapQueryGuardFailureWithoutCallingHandler()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedQuery, QueryHandler, int>()],
            guards: [new RequestGuardRegistration<GuardedQuery, FailingQueryGuard>()]);

        var result = await Bus(provider).SendAsync(new GuardedQuery(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Same(GuardFailure, result.Error);
        Assert.Equal(["query-guard"], calls);
    }

    /// <summary>Successful guards preserve the handler's exact expected failure.</summary>
    [Fact]
    public async Task ShouldPreserveHandlerResultAfterSuccessfulGuards()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<FailingQuery, FailingQueryHandler, int>()],
            guards: [new RequestGuardRegistration<FailingQuery, SuccessfulQueryGuard>()]);

        var result = await Bus(provider).SendAsync(new FailingQuery(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Same(HandlerFailure, result.Error);
        Assert.Equal(["guard", "handler-failure"], calls);
    }

    /// <summary>Matching family and concrete guards run sequentially in registration order.</summary>
    [Fact]
    public async Task ShouldRunMatchingGuardsInRegistrationOrder()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards:
            [
                new RequestGuardRegistration<GuardedCommand, SecondGuard>(),
                new RequestGuardRegistration<IGuardedFamily, FirstFamilyGuard>()
            ]);

        var result = await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["second", "first-family", "handler"], calls);
    }

    /// <summary>Pipeline behaviors wrap guards and the handler at the innermost point.</summary>
    [Fact]
    public async Task ShouldRunGuardsInsidePipelineBehaviors()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            behaviors: [new RequestPipelineBehaviorRegistration<GuardedCommand, WrappingBehavior>(0)],
            guards: [new RequestGuardRegistration<GuardedCommand, RecordingCommandGuard>()]);

        var result = await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System);

        Assert.True(result.IsSuccess);
        Assert.Equal(["behavior-before", "guard", "handler", "behavior-after"], calls);
    }

    /// <summary>An outer behavior that short-circuits does not run guards because no handler executes.</summary>
    [Fact]
    public async Task ShouldSkipGuardsWhenBehaviorShortCircuits()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            behaviors: [new RequestPipelineBehaviorRegistration<GuardedCommand, ShortCircuitBehavior>(0)],
            guards: [new RequestGuardRegistration<GuardedCommand, RecordingCommandGuard>()]);

        var result = await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System);

        Assert.False(result.IsSuccess);
        Assert.Same(GuardFailure, result.Error);
        Assert.Equal(["behavior-short-circuit"], calls);
    }

    /// <summary>AuthorizeAsync remains authorization-only; dispatch authorizes before it runs guards.</summary>
    [Fact]
    public async Task ShouldKeepAuthorizationSeparateAndAheadOfGuards()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            [new RequestAuthorizerRegistration<GuardedCommand, RecordingAuthorizer>()],
            guards: [new RequestGuardRegistration<GuardedCommand, RecordingCommandGuard>()]);
        var bus = Bus(provider);
        var request = new GuardedCommand();

        var authorization = await bus.AuthorizeAsync(request, bus.CreateContext(RequestActor.System));
        Assert.True(authorization.IsSuccess);
        Assert.Equal(["authorizer"], calls);

        calls.Clear();
        var result = await bus.SendAsync(request, RequestActor.System);
        Assert.True(result.IsSuccess);
        Assert.Equal(["authorizer", "guard", "handler"], calls);
    }

    /// <summary>The dispatch cancellation token reaches a guard and cancellation propagates.</summary>
    [Fact]
    public async Task ShouldPropagateGuardCancellation()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards: [new RequestGuardRegistration<GuardedCommand, CancelingGuard>()]);
        using var cancellation = new CancellationTokenSource();
        var probe = provider.GetRequiredService<CancellationProbe>();

        var dispatch = Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System, cancellation.Token).AsTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, probe.Token);
        Assert.Empty(calls);
    }

    /// <summary>Unexpected guard exceptions remain exceptions and skip the handler.</summary>
    [Fact]
    public async Task ShouldPropagateGuardException()
    {
        var calls = new List<string>();
        var expected = new GuardException();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards: [new RequestGuardRegistration<GuardedCommand, ThrowingGuard>()],
            configure: services => services.AddSingleton(expected));

        var error = await Assert.ThrowsAsync<GuardException>(async () =>
            await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System));

        Assert.Same(expected, error);
        Assert.Empty(calls);
    }

    /// <summary>An uninitialized guard result fails loudly and names the application guard.</summary>
    [Fact]
    public async Task ShouldNameGuardReturningUninitializedResult()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new RequestRegistration<GuardedCommand, CommandHandler>()],
            guards: [new RequestGuardRegistration<GuardedCommand, UninitializedGuard>()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Bus(provider).SendAsync(new GuardedCommand(), RequestActor.System));

        Assert.Contains(typeof(UninitializedGuard).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("request guard", error.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    /// <summary>One application-owned request-family guard applies to several concrete requests.</summary>
    [Fact]
    public async Task ShouldReuseFamilyGuardAcrossConcreteRequests()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
        [
            new RequestRegistration<GuardedCommand, CommandHandler>(),
            new RequestRegistration<FamilyQuery, FamilyQueryHandler, int>()
        ], guards: [new RequestGuardRegistration<IGuardedFamily, FirstFamilyGuard>()]);
        var bus = Bus(provider);

        var command = await bus.SendAsync(new GuardedCommand(), RequestActor.System);
        var query = await bus.SendAsync(new FamilyQuery(), RequestActor.System);

        Assert.True(command.IsSuccess);
        Assert.Equal(9, query.Value);
        Assert.Equal(["first-family", "handler", "first-family", "family-query-handler"], calls);
    }

    /// <summary>A guard whose scope matches a streaming request runs before the stream handler.</summary>
    [Fact]
    public async Task ShouldRunGuardsBeforeStreamingHandler()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new StreamRequestRegistration<GuardedStream, StreamHandler, int>()],
            guards: [new RequestGuardRegistration<IRequestBase, BroadGuard>()]);
        var items = new List<int>();

        await foreach (var item in Bus(provider).StreamAsync(new GuardedStream(), RequestActor.System))
            items.Add(item);

        Assert.Equal([4, 5], items);
        Assert.Equal(["broad-guard", "stream-handler"], calls);
    }

    /// <summary>A failed stream guard ends the stream before its first item, carrying the guard's error.</summary>
    [Fact]
    public async Task ShouldFailStreamBeforeFirstItemWhenGuardFails()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new StreamRequestRegistration<GuardedStream, StreamHandler, int>()],
            guards:
            [
                new RequestGuardRegistration<GuardedStream, FailingStreamGuard>(),
                new RequestGuardRegistration<IRequestBase, BroadGuard>()
            ]);
        var items = new List<int>();

        var error = await Assert.ThrowsAsync<RequestGuardException>(async () =>
        {
            await foreach (var item in Bus(provider).StreamAsync(new GuardedStream(), RequestActor.System))
                items.Add(item);
        });

        Assert.Same(GuardFailure, error.Error);
        Assert.Empty(items);
        Assert.Equal(["stream-guard"], calls);
    }

    /// <summary>Stream guards run inside stream behaviors, matching the unary lifecycle order.</summary>
    [Fact]
    public async Task ShouldRunStreamGuardsInsideStreamBehaviors()
    {
        var calls = new List<string>();
        using var provider = Provider(calls,
            [new StreamRequestRegistration<GuardedStream, StreamHandler, int>()],
            behaviors: [new StreamRequestPipelineBehaviorRegistration<GuardedStream, RecordingStreamBehavior, int>(0)],
            guards: [new RequestGuardRegistration<IRequestBase, BroadGuard>()]);

        await foreach (var _ in Bus(provider).StreamAsync(new GuardedStream(), RequestActor.System))
        {
        }

        Assert.Equal(["stream-behavior-before", "broad-guard", "stream-handler", "stream-behavior-after"], calls);
    }

    static IRequestBus Bus(ServiceProvider provider) =>
        provider.GetRequiredService<IRequestBus>();

    static ServiceProvider Provider(List<string> calls, IEnumerable<RequestHandlerRegistration> handlers,
        IEnumerable<RequestAuthorizerRegistration>? authorizers = null,
        IEnumerable<RequestPipelineBehaviorRegistration>? behaviors = null,
        IEnumerable<RequestGuardRegistration>? guards = null,
        Action<IServiceCollection>? configure = null)
    {
        RecordingCommandGuard.Context = null;
        CommandHandler.Context = null;
        var services = new ServiceCollection();
        _ = services.AddSingleton(calls);
        _ = services.AddSingleton<IPermissionEvaluator>(TestPermissionEvaluator.AllowAll());
        _ = services.AddScoped<IRequestBus, RequestBus>();
        _ = services.AddSingleton<RequestRegistry>();
        _ = services.AddScoped<CommandHandler>();
        _ = services.AddScoped<QueryHandler>();
        _ = services.AddScoped<FailingQueryHandler>();
        _ = services.AddScoped<FamilyQueryHandler>();
        _ = services.AddScoped<StreamHandler>();
        _ = services.AddScoped<FailingStreamGuard>();
        _ = services.AddScoped<RecordingStreamBehavior>();
        _ = services.AddScoped<RecordingCommandGuard>();
        _ = services.AddScoped<FailingCommandGuard>();
        _ = services.AddScoped<FailingQueryGuard>();
        _ = services.AddScoped<SuccessfulQueryGuard>();
        _ = services.AddScoped<SecondGuard>();
        _ = services.AddScoped<FirstFamilyGuard>();
        _ = services.AddScoped<BroadGuard>();
        _ = services.AddScoped<CancelingGuard>();
        _ = services.AddScoped<ThrowingGuard>();
        _ = services.AddScoped<UninitializedGuard>();
        _ = services.AddScoped<WrappingBehavior>();
        _ = services.AddScoped<ShortCircuitBehavior>();
        _ = services.AddScoped<RecordingAuthorizer>();
        _ = services.AddSingleton<GuardException>();
        _ = services.AddSingleton<CancellationProbe>();
        foreach (var handler in handlers)
            _ = services.AddSingleton(handler);
        foreach (var authorizer in authorizers ?? [])
            _ = services.AddSingleton(authorizer);
        foreach (var behavior in behaviors ?? [])
            _ = services.AddSingleton(behavior);
        foreach (var guard in guards ?? [])
            _ = services.AddSingleton(guard);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    internal interface IGuardedFamily : IRequestBase;

    internal sealed record GuardedCommand : IRequest, IGuardedFamily;

    internal sealed record GuardedQuery : IRequest<int>;

    internal sealed record FailingQuery : IRequest<int>;

    internal sealed record FamilyQuery : IRequest<int>, IGuardedFamily;

    internal sealed record GuardedStream : IStreamRequest<int>;

    internal sealed class CommandHandler(List<string> calls) : IRequestHandler<GuardedCommand>
    {
        internal static IRequestContext? Context { get; set; }

        public ValueTask<Result> HandleAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            Context = context;
            calls.Add("handler");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class QueryHandler(List<string> calls) : IRequestHandler<GuardedQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<GuardedQuery> context, CancellationToken ct)
        {
            calls.Add("query-handler");
            return ValueTask.FromResult(Result<int>.Success(7));
        }
    }

    internal sealed class FailingQueryHandler(List<string> calls) : IRequestHandler<FailingQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<FailingQuery> context, CancellationToken ct)
        {
            calls.Add("handler-failure");
            return ValueTask.FromResult(Result<int>.Failure(HandlerFailure));
        }
    }

    internal sealed class FamilyQueryHandler(List<string> calls) : IRequestHandler<FamilyQuery, int>
    {
        public ValueTask<Result<int>> HandleAsync(IRequestContext<FamilyQuery> context, CancellationToken ct)
        {
            calls.Add("family-query-handler");
            return ValueTask.FromResult(Result<int>.Success(9));
        }
    }

    internal sealed class StreamHandler(List<string> calls) : IStreamRequestHandler<GuardedStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<GuardedStream> context,
            [EnumeratorCancellation] CancellationToken ct)
        {
            calls.Add("stream-handler");
            yield return 4;
            await Task.Yield();
            yield return 5;
        }
    }

    internal sealed class RecordingCommandGuard(List<string> calls) : IRequestGuard<GuardedCommand>
    {
        internal static IRequestContext? Context { get; set; }

        public ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            Context = context;
            calls.Add("guard");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class FailingCommandGuard(List<string> calls) : IRequestGuard<GuardedCommand>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            calls.Add("failing-guard");
            return ValueTask.FromResult(Result.Failure(GuardFailure));
        }
    }

    internal sealed class FailingQueryGuard(List<string> calls) : IRequestGuard<GuardedQuery>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedQuery> context, CancellationToken ct)
        {
            calls.Add("query-guard");
            return ValueTask.FromResult(Result.Failure(GuardFailure));
        }
    }

    internal sealed class SuccessfulQueryGuard(List<string> calls) : IRequestGuard<FailingQuery>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<FailingQuery> context, CancellationToken ct)
        {
            calls.Add("guard");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class SecondGuard(List<string> calls) : IRequestGuard<GuardedCommand>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            calls.Add("second");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class FirstFamilyGuard(List<string> calls) : IRequestGuard<IGuardedFamily>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<IGuardedFamily> context, CancellationToken ct)
        {
            calls.Add("first-family");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class FailingStreamGuard(List<string> calls) : IRequestGuard<GuardedStream>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedStream> context, CancellationToken ct)
        {
            calls.Add("stream-guard");
            return ValueTask.FromResult(Result.Failure(GuardFailure));
        }
    }

    internal sealed class RecordingStreamBehavior(List<string> calls)
        : IStreamRequestPipelineBehavior<GuardedStream, int>
    {
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<GuardedStream> context,
            StreamRequestPipelineNext<int> next, [EnumeratorCancellation] CancellationToken ct)
        {
            calls.Add("stream-behavior-before");
            await foreach (var item in next(ct).WithCancellation(ct))
                yield return item;
            calls.Add("stream-behavior-after");
        }
    }

    internal sealed class BroadGuard(List<string> calls) : IRequestGuard<IRequestBase>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<IRequestBase> context, CancellationToken ct)
        {
            calls.Add("broad-guard");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class CancelingGuard(CancellationProbe probe) : IRequestGuard<GuardedCommand>
    {
        public async ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            probe.Token = ct;
            _ = probe.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Result.Success;
        }
    }

    internal sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; set; }
    }

    internal sealed class ThrowingGuard(GuardException error) : IRequestGuard<GuardedCommand>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct) =>
            ValueTask.FromException<Result>(error);
    }

    internal sealed class UninitializedGuard : IRequestGuard<GuardedCommand>
    {
        public ValueTask<Result> GuardAsync(IRequestContext<GuardedCommand> context, CancellationToken ct) => default;
    }

    internal sealed class WrappingBehavior(List<string> calls) : IRequestPipelineBehavior<GuardedCommand>
    {
        public async ValueTask<Result> HandleAsync(IRequestContext<GuardedCommand> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            calls.Add("behavior-before");
            var result = await continuation(ct);
            calls.Add("behavior-after");
            return result;
        }
    }

    internal sealed class ShortCircuitBehavior(List<string> calls) : IRequestPipelineBehavior<GuardedCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<GuardedCommand> context,
            RequestPipelineNext continuation, CancellationToken ct)
        {
            calls.Add("behavior-short-circuit");
            return ValueTask.FromResult(Result.Failure(GuardFailure));
        }
    }

    internal sealed class RecordingAuthorizer(List<string> calls) : IRequestAuthorizer<GuardedCommand>
    {
        public ValueTask<Result> AuthorizeAsync(IRequestContext<GuardedCommand> context, CancellationToken ct)
        {
            calls.Add("authorizer");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class GuardException : Exception;
}
