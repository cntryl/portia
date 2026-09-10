namespace Cntryl.Portia.Consumer;

public sealed class FeatureOneHandler : IRequestHandler<FeatureOneRequest, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<FeatureOneRequest> context, CancellationToken ct)
        => ValueTask.FromResult(Result<int>.Success(context.Request.Value + 1));
}
