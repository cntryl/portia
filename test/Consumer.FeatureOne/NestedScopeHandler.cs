namespace Cntryl.Portia.Consumer;

public sealed class NestedScopeHandler(IConsumerScope scope, IConsumerEffects effects)
    : IRequestHandler<NestedScopeRequest>
{
    public ValueTask<Result> HandleAsync(IRequestContext<NestedScopeRequest> context, CancellationToken ct)
    {
        effects.Record("nested", context.Request.Id, 0, scope.Id);
        return ValueTask.FromResult(Result.Success);
    }
}
