namespace Cntryl.Portia;

sealed class TelemetryGuardedActionHandler : IRequestHandler<TelemetryGuardedAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetryGuardedAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
