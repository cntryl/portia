namespace Cntryl.Portia;

sealed class AllocationProbeActionHandler : IRequestHandler<AllocationProbeAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<AllocationProbeAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
