namespace Cntryl.Portia.Consumer;

public sealed class FeatureTwoHandler : IRequestHandler<FeatureTwoRequest, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<FeatureTwoRequest> context, CancellationToken ct)
        => ValueTask.FromResult(Result<int>.Success(context.Request.Value + 2));
}

[Discriminator("FeatureTwoObserved")]
public sealed record FeatureTwoObserved(int Value) : DomainEvent;
