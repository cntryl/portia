namespace Cntryl.Portia;

static class RequestGuardRunner
{
    internal static async ValueTask<Result> RunAsync(IServiceProvider services,
        RequestGuardRegistration[] guards, IRequestBase request, IRequestContext context, CancellationToken ct)
    {
        foreach (var guard in guards)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var outcome = "fault";
            Result result;
            try
            {
                result = await guard.GuardAsync(services, request, context, ct).ConfigureAwait(false);
                Validate(result, guard.GuardType);
                outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            }
            catch (Exception exception)
            {
                outcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
                throw;
            }
            finally
            {
                PortiaTelemetry.GuardFinished(started, guard.ComponentName, outcome);
            }

            if (!result.IsSuccess)
                return result;
        }

        return Result.Success;
    }

    static void Validate(Result result, Type guardType)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia request guard '{guardType.FullName}' returned an uninitialized Result.", ex);
        }
    }
}
