namespace Cntryl.Portia.Testing;

// Cause is the exception a streamed request ended with when the bus may treat it as settled; the engine rethrows
// it when the observed lifecycle shows the bus would have reported a fault instead.
sealed record ScenarioOutcome(bool IsSuccess, RequestError? Error, Exception? Cause = null)
{
    public static ScenarioOutcome From(bool isSuccess, RequestError? error) => new(isSuccess, error);

    public static ScenarioOutcome Conflict() => new(false, RequestBus.ConcurrencyError());

    public override string ToString() => IsSuccess ? "success" : $"failure({Error!.Kind}) \"{Error.Message}\"";
}
