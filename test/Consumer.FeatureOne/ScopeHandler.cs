using System.Security.Claims;

namespace Cntryl.Portia.Consumer;

public sealed class ScopeHandler(IRequestBus bus, IConsumerScope scope, IConsumerEffects effects) : IRequestHandler<ScopeRequest>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<ScopeRequest> context, CancellationToken ct)
    {
        effects.Record("delivery", context.Request.Id, context.Request.Behavior, scope.Id);
        _ = await bus.SendAsync(new NestedScopeRequest(context.Request.Id), new ClaimsPrincipal(), ct);
        if (context.Request.Behavior == 1)
            throw new InvalidOperationException("Consumer failure");
        if (context.Request.Behavior == 2)
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Result.Success;
    }
}

public sealed class NestedScopeHandler(IConsumerScope scope, IConsumerEffects effects) : IRequestHandler<NestedScopeRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<NestedScopeRequest> context, CancellationToken ct)
    {
        effects.Record("nested", context.Request.Id, 0, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }
}
