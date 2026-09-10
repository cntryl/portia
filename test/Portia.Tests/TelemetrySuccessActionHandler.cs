namespace Cntryl.Portia;

sealed class TelemetrySuccessActionHandler : IRequestHandler<TelemetrySuccessAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetrySuccessAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
