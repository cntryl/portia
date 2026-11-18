using System.Diagnostics;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record CancellationObservation(string Status, double? CancelToEndMilliseconds, string? Error);
sealed record CancellationReport(string Case, string Size, CancellationObservation[] Observations, string Detail,
    ToolingIdentity Identity, string FixtureHash);

static class CancellationProbe
{
    public static async Task<CancellationObservation> ObserveAsync(BenchmarkCase operation,
        CancellationTokenSource cancellation, int delayMilliseconds = 1)
    {
        if (delayMilliseconds is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Task.Run(async () =>
        {
            entered.SetResult();
            await operation.ExecuteAsync().ConfigureAwait(false);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Task.Delay(delayMilliseconds, CancellationToken.None).ConfigureAwait(false);
        if (execution.IsCompleted)
        {
            try
            { await execution.ConfigureAwait(false); }
            catch (Exception error) { return new CancellationObservation("faulted-before-cancel", null, error.ToString()); }
            return new CancellationObservation("completed-before-cancel", null, null);
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            await Task.WhenAll(cancellation.CancelAsync(), execution).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new CancellationObservation("completed-after-cancel", Stopwatch.GetElapsedTime(start).TotalMilliseconds, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new CancellationObservation("canceled-after-entry", Stopwatch.GetElapsedTime(start).TotalMilliseconds, null);
        }
        catch (TimeoutException)
        {
            _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new CancellationObservation("unresponsive", Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                "Request did not stop within five seconds; terminate this probe process before another trial.");
        }
        catch (Exception error)
        {
            return new CancellationObservation("faulted-after-cancel", Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString());
        }
    }

    public static async Task<CancellationReport> RunAsync(CaseSpec spec, string size, int trials, int delay,
        CancellationToken ct)
    {
        if (spec.Kind == "fixes" && spec.Operation is not ("discover" or "discover-cached" or "preview" or "compute"))
            throw new ArgumentException("Cancellation probes cover code-fix computation; applying a workspace change is a separate commit boundary.", nameof(spec));
        var results = new List<CancellationObservation>();
        for (var i = 0; i < trials; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var operation = await BenchmarkCase.PrepareAsync(spec, size, cancel.Token).ConfigureAwait(false);
            var result = await ObserveAsync(operation, cancel, delay).ConfigureAwait(false);
            results.Add(result);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, Campaign.Json));
            if (result.Error is not null)
                break;
        }
        return new CancellationReport(spec.Id, size, results.ToArray(),
            "Cancellation requested after public request entry; not a claim about a specific callback. Completed-before-cancel requests are separate. Five-second response watchdog. Latencies diagnostic until collected on an accepted host; code-fix publication is excluded.",
            Campaign.Identity(), Campaign.FixtureHash(spec, size));
    }
}
