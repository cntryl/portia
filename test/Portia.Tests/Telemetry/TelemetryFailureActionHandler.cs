namespace Cntryl.Portia;

sealed class TelemetryFailureActionHandler : IRequestHandler<TelemetryFailureAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetryFailureAction> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Invalid.")));
}
