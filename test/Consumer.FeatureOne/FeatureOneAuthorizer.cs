namespace Cntryl.Portia.Consumer;

public sealed class FeatureOneAuthorizer : IRequestAuthorizer<FeatureOneRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<FeatureOneRequest> context, CancellationToken ct)
        => ValueTask.FromResult(Result.Success);
}
