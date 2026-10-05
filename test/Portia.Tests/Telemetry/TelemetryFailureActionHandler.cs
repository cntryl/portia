namespace Cntryl.Portia;

sealed class TelemetryFailureActionHandler(Exception? exception = null) : IRequestHandler<TelemetryFailureAction>
{
    public ValueTask<Result> HandleAsync(IRequestContext<TelemetryFailureAction> context, CancellationToken ct) =>
        exception is not null ? ValueTask.FromException<Result>(exception)
            : ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Validation, "Invalid.")));
}
