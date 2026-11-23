using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record RetentionCheckpoint(int Edits, long ManagedBytes, long WorkingSetBytes, int RetiredCompilationsAlive);
sealed record RetentionReport(string Status, string Size, int EditsCompleted, RetentionCheckpoint[] Checkpoints,
    double[] RequestMilliseconds, long GrowthAllowanceBytes, HostCounters Before, HostCounters After,
    string Detail, ToolingIdentity Identity, string FixtureHash);

static class RetentionProbe
{
    sealed record State(ApplicationFixture Fixture, ApplicationBuild Graph);

    public static async Task<RetentionReport> RunAsync(
        string size,
        int edits,
        Action<RetentionCheckpoint>? checkpoint = null,
        CancellationToken ct = default
    )
    {
        if (edits is < 2 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(edits));

        var before = HostCounters.Capture();
        var state = Start(size);
        var expected = state.Graph.Sources();
        var retired = new List<WeakReference<CSharpCompilation>>();
        var memory = new List<RetentionCheckpoint> { Memory(0, retired) };
        checkpoint?.Invoke(memory[0]);
        var requests = new List<double>();
        var points = new HashSet<int> { Math.Min(100, edits), Math.Min(500, edits), edits };
        var completed = 0;

        try
        {
            for (var i = 0; i < edits; i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp();
                state = Advance(state, i, retired, ct);
                var diagnostics = await BenchmarkCase.AnalyzeDocumentAsync(state.Graph, "body", ct).ConfigureAwait(false);
                requests.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

                if (diagnostics.Length != 0)
                    throw new InvalidOperationException(string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString())));

                completed++;

                if (!points.Contains(completed))
                    continue;

                var row = Memory(completed, retired);
                memory.Add(row);
                checkpoint?.Invoke(row);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RetentionReport("canceled", size, completed, memory.ToArray(), requests.ToArray(), 0,
                before, HostCounters.Capture(), "Session interrupted; completed checkpoints retained; no retention verdict.",
                Campaign.Identity(), state.Fixture.Identity);
        }

        Verification.Changes(expected, state.Graph.Sources(), []);

        foreach (var project in state.Graph.Projects)
            CompilerHost.CheckRun(project.Driver, project.Input);

        var warm = memory.First(row => row.Edits == Math.Min(100, edits));
        var allowance = Math.Max((long)(warm.ManagedBytes * .10), 8L * 1024 * 1024);
        var last = memory[^1];
        var within = last.ManagedBytes - warm.ManagedBytes <= allowance
            && memory.All(row => row.RetiredCompilationsAlive <= 2);
        return new RetentionReport(within ? (edits == 1000 ? "within-budget" : "smoke-passed") : "retention-failed", size, completed, memory.ToArray(),
            requests.ToArray(), allowance, before, HostCounters.Capture(),
            "instrumented generation + changed-document analysis session; only current graph retained; weak references to retired Core outputs; full GC outside per-request timers; post-warm managed growth <= max(10%, 8 MiB); at most two retired outputs alive; fewer than 1000 edits is a smoke check; request latencies diagnostic only",
            Campaign.Identity(), state.Fixture.Identity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static State Start(string size)
    {
        var fixture = ApplicationFixture.Create(size);
        return new State(fixture, fixture.Build());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static State Advance(State previous, int index, List<WeakReference<CSharpCompilation>> retired, CancellationToken ct)
    {
        var fixture = previous.Fixture.Edit(index % 2 == 0 ? "body" : "body-restore");
        var graph = fixture.Build(previous.Graph, ct);
        retired.Add(new WeakReference<CSharpCompilation>(previous.Graph.Projects.Single(project => project.Role == "Core").Output));
        return new State(fixture, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static RetentionCheckpoint Memory(int edits, List<WeakReference<CSharpCompilation>> retired)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return new RetentionCheckpoint(edits, GC.GetTotalMemory(forceFullCollection: false), process.WorkingSet64,
            retired.Count(reference => reference.TryGetTarget(out _)));
    }
}
