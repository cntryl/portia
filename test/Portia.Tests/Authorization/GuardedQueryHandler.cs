namespace Cntryl.Portia;

sealed class GuardedQueryHandler : IRequestHandler<GuardedQuery, int>
{
    public ValueTask<Result<int>> HandleAsync(IRequestContext<GuardedQuery> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<int>.Success(1));
}
