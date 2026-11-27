using System.Diagnostics;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record CancellationObservation(string Status, double? CancelToEndMilliseconds, string? Error,
    bool? WorkspaceUnchanged = null);
sealed record CancellationReport(string Case, string Size, CancellationObservation[] Observations, string Detail,
    ToolingIdentity Identity, string FixtureHash);

static class CancellationProbe
{
    internal static async Task<CancellationObservation> RunTrialAsync(
        Func<CancellationToken, Task<BenchmarkCase>> prepare,
        int delay,
        CancellationToken ct
    )
    {
        if (delay is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(delay));

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        BenchmarkCase? operation = null;
        var pending = Task.CompletedTask;

        try
        {
            operation = await prepare(cancellation.Token).ConfigureAwait(false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var execution = Task.Run(async () =>
            {
                entered.SetResult();
                await operation.ExecuteAsync().ConfigureAwait(false);
            }, CancellationToken.None);
            pending = execution;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

            if (execution.IsCompleted)
            {
                try
                {
                    await execution.ConfigureAwait(false);
                    return CheckWorkspace(new("completed-before-cancel", null, null));
                }
                catch (Exception error)
                {
                    return CheckWorkspace(new("faulted-before-cancel", null, error.ToString()));
                }
            }


            var start = Stopwatch.GetTimestamp();
            pending = Task.WhenAll(cancellation.CancelAsync(), execution);
            using var timer = new CancellationTokenSource();
            var deadline = Task.Delay(TimeSpan.FromSeconds(5), timer.Token);

            if (await Task.WhenAny(pending, deadline).ConfigureAwait(false) == deadline)
                return new("unresponsive", Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    "Request did not stop within five seconds; terminate this probe process before another trial.");


            await timer.CancelAsync().ConfigureAwait(false);

            try
            {
                await pending.ConfigureAwait(false);
                return CheckWorkspace(new("completed-after-cancel", Stopwatch.GetElapsedTime(start).TotalMilliseconds, null));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return CheckWorkspace(new("canceled-after-entry", Stopwatch.GetElapsedTime(start).TotalMilliseconds, null));
            }
            catch (Exception error)
            {
                return CheckWorkspace(new("faulted-after-cancel", Stopwatch.GetElapsedTime(start).TotalMilliseconds, error.ToString()));
            }
        }
        finally
        {
            // A watchdog returns a measurement while the compiler or its cancellation callbacks
            // can still be using the workspace. Their completion owns disposal in that case.
            if (pending.IsCompleted)
            {
                _ = pending.Exception;
                DisposeResources();
            }
            else
                _ = DisposeWhenStoppedAsync(pending);
        }

        CancellationObservation CheckWorkspace(CancellationObservation observation)
        {
            var unchanged = operation?.WorkspaceUnchanged();

            if (unchanged is not false)
                return observation with { WorkspaceUnchanged = unchanged };

            const string publication = "Code-fix computation changed the workspace before an explicit apply.";
            return observation with
            {
                WorkspaceUnchanged = false,
                Error = observation.Error is null ? publication : publication + Environment.NewLine + observation.Error
            };
        }

        void DisposeResources()
        {
            using (cancellation)
                operation?.Dispose();
        }

        async Task DisposeWhenStoppedAsync(Task lifetime)
        {
            await lifetime.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            DisposeResources();
        }
    }

    public static async Task<CancellationReport> RunAsync(
        CaseSpec spec,
        string size,
        int trials,
        int delay,
        CancellationToken ct
    )
    {
        if (spec.Kind == "fixes" && spec.Operation is not ("discover" or "discover-cached" or "preview" or "compute"))
            throw new ArgumentException("Cancellation probes cover code-fix computation; applying a workspace change is a separate commit boundary.", nameof(spec));

        var results = new List<CancellationObservation>();

        for (var i = 0; i < trials; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunTrialAsync(token => BenchmarkCase.PrepareAsync(spec, size, token), delay, ct)
                .ConfigureAwait(false);
            results.Add(result);
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, Campaign.Json));

            if (result.Error is not null)
                break;
        }

        return new CancellationReport(spec.Id, size, results.ToArray(),
            "Cancellation requested after public request entry; not a claim about a specific callback. Completed-before-cancel requests are separate. Five-second response watchdog. Latencies diagnostic until collected on an accepted host. Settled code-fix trials verify unchanged workspace identity; explicit apply is excluded.",
            Campaign.Identity(), Campaign.FixtureHash(spec, size));
    }
}
