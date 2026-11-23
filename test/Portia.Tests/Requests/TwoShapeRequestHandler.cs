namespace Cntryl.Portia;

sealed class TwoShapeRequestHandler : IRequestHandler<TwoShapeRequest>, IRequestHandler<TwoShapeRequest, int>
{
    ValueTask<Result> IRequestHandler<TwoShapeRequest>.HandleAsync(
        IRequestContext<TwoShapeRequest> context,
        CancellationToken ct
    ) => ValueTask.FromResult(Result.Success);

    ValueTask<Result<int>> IRequestHandler<TwoShapeRequest, int>.HandleAsync(
        IRequestContext<TwoShapeRequest> context,
        CancellationToken ct
    ) =>
        ValueTask.FromResult(Result<int>.Success(1));
}
