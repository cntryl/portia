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
    static readonly JsonSerializerOptions ArtifactJsonOptions = new() { WriteIndented = true };
    readonly string _endpoint = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT")
                                ?? "ws://127.0.0.1:4090/ws";
    FitzClient _client = null!;
    FitzOperationalStore _journal = null!;
    OperationalTarget[] _targets = [];
    string _route = null!;
    int _serializedStateBytes;

    /// <summary>Number of retained attempt receipts in each workload document.</summary>
    [Params(4, 128)]
    public int History { get; set; }

    /// <summary>Number of independent workload documents participating in a shared outage.</summary>
    [Params(1, 8)]
    public int Workloads { get; set; }

    /// <summary>Runs the real-Fitz matrix without BenchmarkDotNet's runtime validation.</summary>
    public static async Task RunDirectAsync()
    {
        var rows = new List<object>();
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
                    for (var i = 0; i < 10; i++)
                        await operation();
                    var samples = new double[50];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        await operation();
                        samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        await benchmark.ValidateReadbackAsync(name);
                    }

                    var sorted = samples.OrderBy(value => value).ToArray();
                    var operations = benchmark.OperationsPerInvocation(name);
                    rows.Add(new
                    {
                        history,
                        workloads,
                        operation = name,
                        samples,
                        samples_count = samples.Length,
                        p50_ms = Percentile(sorted, .50),
                        p95_ms = Percentile(sorted, .95),
                        p99_ms = Percentile(sorted, .99),
                        operations_per_invocation = operations,
                        operations_per_second = operations / (Percentile(sorted, .50) / 1000),
                        serialized_state_bytes = benchmark._serializedStateBytes
                    });
                }
            }

        var artifact = Environment.GetEnvironmentVariable("PORTIA_OPERATIONAL_BASELINE_ARTIFACT")
                       ?? Path.Combine("BenchmarkDotNet.Artifacts", "results", "operational-journal-baseline.json");
        var endpoint = new Uri(Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(artifact))!);
        await File.WriteAllTextAsync(artifact, JsonSerializer.Serialize(new
        {
            generated_utc = DateTimeOffset.UtcNow,
            commit = Environment.GetEnvironmentVariable("PORTIA_BASELINE_COMMIT") ?? "unknown",
            runtime = Environment.Version.ToString(),
            sdk = Environment.GetEnvironmentVariable("PORTIA_BASELINE_SDK"),
            endpoint = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port).Uri.GetLeftPart(UriPartial.Authority),
            broker_image = Environment.GetEnvironmentVariable("PORTIA_BASELINE_BROKER_IMAGE"),
            os = Environment.OSVersion.ToString(),
            processor_count = Environment.ProcessorCount,
            rows
        }, ArtifactJsonOptions));
        Console.WriteLine($"artifact={Path.GetFullPath(artifact)}");

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
        {
            var created = await _journal.CompareExchangeAsync(CreateState(target), 0, Guid.Empty);
            if (!created)
                throw new InvalidOperationException("Failed to seed an isolated operational baseline document.");
        }
        _serializedStateBytes = await ReadSerializedStateBytesAsync(_targets[0]);
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
        if (!await _journal.CompareExchangeAsync(replacement, state.Revision, state.OwnerSession))
            throw new InvalidOperationException("Baseline retry transition lost its fenced compare-and-exchange.");
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
        var committed = await Task.WhenAll(states.Select(state =>
        {
            var next = state! with { Revision = checked(state.Revision + 1), ConsecutiveFailures = 0, Block = null };
            return _journal.CompareExchangeAsync(next, state!.Revision, state.OwnerSession).AsTask();
        }));
        if (committed.Any(value => !value))
            throw new InvalidOperationException("Baseline recovery lost a fenced compare-and-exchange.");
    }

    async Task ValidateReadbackAsync(string operation)
    {
        if (operation is "read" or "retry")
        {
            if (await _journal.ReadAsync(_targets[0]) is null)
                throw new InvalidOperationException("Baseline readback found no operational document.");
        }
        else if (operation == "status")
        {
            if ((await _journal.ListAsync(OperationalScope.Global)).Count != Workloads)
                throw new InvalidOperationException("Baseline status readback returned the wrong workload count.");
        }
        else
        {
            var states = await Task.WhenAll(_targets.Select(target => _journal.ReadAsync(target).AsTask()));
            if (states.Any(state => state is null))
                throw new InvalidOperationException("Baseline recovery readback found no operational document.");
        }
        _serializedStateBytes = await ReadSerializedStateBytesAsync(_targets[0]);
    }

    int OperationsPerInvocation(string operation) => operation switch
    {
        "retry" => 2,
        "status" => Workloads + 1,
        "recovery" => Workloads * 2,
        _ => 1
    };

    async Task<int> ReadSerializedStateBytesAsync(OperationalTarget target)
    {
        var state = await _journal.ReadAsync(target)
                    ?? throw new InvalidOperationException("Baseline document disappeared during measurement.");
        return JsonSerializer.SerializeToUtf8Bytes(state, FitzJsonContext.Default.OperationalProcessorState).Length;
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
        return state;
    }
}
