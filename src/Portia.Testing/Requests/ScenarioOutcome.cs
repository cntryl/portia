namespace Cntryl.Portia.Testing;

sealed record ScenarioOutcome(bool IsSuccess, RequestError? Error)
{
    public static ScenarioOutcome From(bool isSuccess, RequestError? error) => new(isSuccess, error);

    public override string ToString() => IsSuccess ? "success" : $"failure({Error!.Kind}) \"{Error.Message}\"";
}
