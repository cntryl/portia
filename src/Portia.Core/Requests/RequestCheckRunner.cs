namespace Cntryl.Portia;

static class RequestCheckRunner
{
    internal static async ValueTask<Result> RunAsync<TCheck>(
        IServiceProvider services,
        TCheck[] checks,
        IRequestBase request,
        IRequestContext context,
        CancellationToken ct
    )
        where TCheck : class, IRequestCheck
    {
        foreach (var check in checks)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var outcome = "fault";
            Result result;

            try
            {
                result = await check.CheckAsync(services, request, context, ct).ConfigureAwait(false);
                RequestResults.Validate(result, check.Owner, check.Role);
                outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            }
            catch (Exception exception)
            {
                outcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
                throw;
            }
            finally
            {
                check.Finished(started, outcome);
            }


            if (!result.IsSuccess)
                return result;
        }

        return Result.Success;
    }
}
