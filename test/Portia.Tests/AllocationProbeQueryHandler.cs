namespace Cntryl.Portia;

sealed class AllocationProbeQueryHandler : IRequestHandler<AllocationProbeQuery, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<AllocationProbeQuery> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success("probe"));
}
