namespace Cntryl.Portia.Consumer;

public sealed class FeatureOneHandler : IRequestHandler<FeatureOneRequest, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<FeatureOneRequest> context, CancellationToken ct)
        => ValueTask.FromResult(Result<int>.Success(context.Request.Value + 1));
}

[Discriminator("FeatureOneObserved")]
public sealed record FeatureOneObserved(int Value) : DomainEvent;

public sealed class FeatureOneAuthorizer : IRequestAuthorizer<FeatureOneRequest>
{
    public ValueTask<Result> AuthorizeAsync(IRequestContext<FeatureOneRequest> context, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default)
        => ValueTask.FromResult(Result.Success);
}
