using System.Diagnostics;
using System.Text.Json;
using Cntryl.Fitz;

namespace Cntryl.Portia;

/// <summary>
/// Measures the real Fitz operational journal and status read paths without changing durability.
/// The benchmark mutates isolated workload documents through the same fenced store API used by
/// workers; it does not substitute an in-memory provider or bypass journal transactions.
/// </summary>
[MemoryDiagnoser]
public sealed class OperationalJournalBaselineBenchmarks : IAsyncDisposable
{
    readonly string _endpoint = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT")
                                ?? "ws://127.0.0.1:4090/ws";
    FitzClient _client = null!;
    FitzOperationalStore _journal = null!;
    OperationalTarget[] _targets = [];
    string _route = null!;

    /// <summary>Number of retained attempt receipts in each workload document.</summary>
    [Params(4, 128)]
    public int History { get; set; }

    /// <summary>Number of independent workload documents participating in a shared outage.</summary>
    [Params(1, 8)]
    public int Workloads { get; set; }

    /// <summary>Runs the real-Fitz matrix without BenchmarkDotNet's runtime validation.</summary>
    public static async Task RunDirectAsync()
    {
        Console.WriteLine("history,workloads,operation,samples,p50_ms,p95_ms,p99_ms");
        foreach (var history in new[] { 4, 128 })
            foreach (var workloads in new[] { 1, 8 })
            {
                await using var benchmark = new OperationalJournalBaselineBenchmarks
                {
                    History = history,
                    Workloads = workloads
                };
                await benchmark.SetupAsync();
                foreach (var (name, operation) in new (string, Func<Task>)[]
                         {
                         ("read", () => benchmark.ReadRetainedDocument().AsTask()),
                         ("retry", () => benchmark.FencedRetryTransition().AsTask()),
                         ("status", () => benchmark.SharedOutageStatusLoad().AsTask()),
                         ("recovery", benchmark.SharedOutageRecovery)
                         })
                {
                    for (var i = 0; i < 3; i++)
                        await operation();
                    var samples = new double[20];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        await operation();
                        samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }

                    Array.Sort(samples);
                    Console.WriteLine($"{history},{workloads},{name},{samples.Length},"
                                      + $"{Percentile(samples, .50):F3},{Percentile(samples, .95):F3},"
                                      + $"{Percentile(samples, .99):F3}");
                }
            }

        static double Percentile(double[] sorted, double percentile)
            => sorted[(int)Math.Clamp(Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Connects to Fitz and creates isolated baseline documents.</summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _client = new FitzClient(new ClientConfig(new Uri(_endpoint)) { Timeout = TimeSpan.FromSeconds(10) });
        await _client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)));
        _route = "kv://benchmarks/operations/" + Guid.NewGuid().ToString("N");
        _journal = new FitzOperationalStore(_client.Kv, _route);
        _targets = Enumerable.Range(0, Workloads)
            .Select(i => new OperationalTarget($"baseline-{i}", "stream://baseline/source/*", OperationalScope.Global))
            .ToArray();
        foreach (var target in _targets)
            await _journal.CompareExchangeAsync(CreateState(target), 0, Guid.Empty);
    }

    /// <summary>Reads one retained journal document, representing a normal status/pass read.</summary>
    [Benchmark]
    public ValueTask<OperationalProcessorState?> ReadRetainedDocument()
        => _journal.ReadAsync(_targets[0]);

    /// <summary>Writes one fenced retry transition and measures synchronous journal serialization.</summary>
    [Benchmark]
    public async ValueTask FencedRetryTransition()
    {
        var state = (await _journal.ReadAsync(_targets[0]))!;
        var replacement = state with { Revision = checked(state.Revision + 1), ConsecutiveFailures = state.ConsecutiveFailures + 1 };
        _ = await _journal.CompareExchangeAsync(replacement, state.Revision, state.OwnerSession);
    }

    /// <summary>Loads all workload status documents after a shared outage.</summary>
    [Benchmark]
    public async ValueTask<IReadOnlyList<OperationalProcessorState>> SharedOutageStatusLoad()
        => await _journal.ListAsync(OperationalScope.Global);

    /// <summary>Performs fenced recovery transitions concurrently for independent workloads.</summary>
    [Benchmark]
    public async Task SharedOutageRecovery()
    {
        var states = await Task.WhenAll(_targets.Select(target => _journal.ReadAsync(target).AsTask()));
        await Task.WhenAll(states.Select(state =>
        {
            var next = state! with { Revision = checked(state.Revision + 1), ConsecutiveFailures = 0, Block = null };
            return _journal.CompareExchangeAsync(next, state!.Revision, state.OwnerSession).AsTask();
        }));
    }

    /// <summary>Releases the real Fitz client.</summary>
    [GlobalCleanup]
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    OperationalProcessorState CreateState(OperationalTarget target)
    {
        var owner = Guid.NewGuid();
        var settings = new OperationalProcessingSettings(32, 512, 3, TimeSpan.FromMilliseconds(10).Ticks,
            TimeSpan.FromSeconds(2).Ticks);
        var attempts = Enumerable.Range(0, History).Select(index => new OperationalAttempt(
            Guid.NewGuid(), owner, DateTimeOffset.UnixEpoch.AddSeconds(index), DateTimeOffset.UnixEpoch.AddSeconds(index + 1),
            index == History - 1 ? OperationalAttemptOutcomes.Failed : OperationalAttemptOutcomes.Committed,
            index == 0 ? null : $"cursor-{index - 1}", $"cursor-{index}",
            [new OperationalSource("stream://baseline/source/item", "event", (ulong)index, $"cursor-{index}")], [],
            "baseline", new ApplicationTopology([], [], "baseline"),
            index == History - 1 ? "Handling" : null, index == History - 1 ? "SharedOutage" : null,
            index == History - 1));
        var state = new OperationalProcessorState(target, ProcessorKinds.Reactor, owner, 1,
            $"cursor-{History - 1}", settings, attempts.ToArray(), null, [], [], [], 0, DateTimeOffset.UnixEpoch);
        // Keep the byte-size calculation in the harness output and make the serialized shape explicit.
        _ = JsonSerializer.SerializeToUtf8Bytes(state);
        return state;
    }
}
