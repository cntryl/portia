using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Cntryl.Fitz;

namespace Cntryl.Portia;

static class OperationalWorkerBenchmark
{
    static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };
    static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(50);

    internal static async Task RunAsync()
    {
        var artifact = Required("PORTIA_OPERATIONAL_WORKER_ARTIFACT");
        var revision = Required("PORTIA_OPERATIONAL_WORKER_COMMIT");
        var sdk = Required("PORTIA_OPERATIONAL_WORKER_SDK");
        var broker = Required("PORTIA_OPERATIONAL_WORKER_BROKER");
        var machine = Required("PORTIA_OPERATIONAL_WORKER_MACHINE");
        var endpoint = new Uri(Required("FITZ_TEST_ENDPOINT"));
        var smoke = Environment.GetEnvironmentVariable("PORTIA_OPERATIONAL_WORKER") == "smoke";
        var rows = new List<WorkloadRow>();

        using var terminal = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            terminal.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(artifact))!);
        var incomplete = artifact + ".incomplete";

        try
        {
            await using var client = new FitzClient(new ClientConfig(endpoint) { Timeout = TimeSpan.FromSeconds(10) });
            await client.ConnectWhenReadyAsync(new ConnectWhenReadyOptions(TimeSpan.FromSeconds(15)), terminal.Token);

            foreach (var history in smoke ? [4] : new[] { 4, 128 })
                foreach (var workloadCount in smoke ? [1] : new[] { 1, 8 })
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(terminal.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var row = await MeasureAsync(client, history, workloadCount, smoke ? 2 : 20, timeout.Token);
                    rows.Add(row);
                    await SaveAsync(incomplete, complete: false);
                    Console.WriteLine($"verified history={history} workloads={workloadCount}");
                }

            await SaveAsync(incomplete, complete: true);
            File.Move(incomplete, artifact, overwrite: true);
            Console.WriteLine($"artifact={Path.GetFullPath(artifact)}");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        Task SaveAsync(string path, bool complete) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            complete,
            smoke,
            generatedUtc = DateTimeOffset.UtcNow,
            revision,
            sdk,
            runtime = RuntimeInformation.FrameworkDescription,
            dependencies = new[] { typeof(FitzClient).Assembly, typeof(FitzOperationalStore).Assembly }
                .Select(assembly => new { name = assembly.GetName().Name, version = assembly.GetName().Version }),
            endpoint = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port).Uri.GetLeftPart(UriPartial.Authority),
            broker,
            machine,
            storage = "durable local Fitz, synchronous projection / operational journal commits",
            statusProbeIntervalMs = ProbeInterval.TotalMilliseconds,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            rows
        }, ArtifactJson));
    }

    static async Task<WorkloadRow> MeasureAsync(
        FitzClient client,
        int history,
        int workloadCount,
        int eventsPerPhase,
        CancellationToken ct
    )
    {
        var realm = $"journal-{Guid.NewGuid():N}";
        var journal = $"kv://{realm}/performance/journal";
        var outage = new JournalBenchmarkOutage();
        var hosts = new List<JournalBenchmarkHost>(workloadCount);

        try
        {
            for (var index = 0; index < workloadCount; index++)
            {
                var host = new JournalBenchmarkHost(client, new JournalBenchmarkWorkload(realm, index, outage), journal);
                hosts.Add(host);
                await host.StartAsync(ct);
                _ = await WaitAsync(host, state => state.OwnerSession != Guid.Empty, ct);
            }

            var seeded = await Together(async host =>
            {
                var cursor = await host.Workload.PublishAsync(host.Events, history, ct);
                var state = await WaitAsync(host, s => s.Attempts.Count == history && s.Checkpoint == cursor.Value, ct);
                await VerifyNativeAsync(host, cursor, applied: true, ct);
                return state;
            });

            var beforeNormal = await Together(host => SnapshotAsync(host, ct));
            var normal = await InPhaseAsync(hosts, "normal", async () =>
            {
                return await Together(async host =>
                {
                    var cursor = await host.Workload.PublishAsync(host.Events, eventsPerPhase, ct);
                    var state = await WaitAsync(host, s => s.Checkpoint == cursor.Value, ct);
                    await VerifyNativeAsync(host, cursor, applied: true, ct);
                    return state;
                });
            }, ct);

            var cursorBeforeOutage = normal.States.Select(state => new EventCursor(state.Checkpoint!)).ToArray();
            var beforeNormalByName = beforeNormal.ToDictionary(snapshot => snapshot.Workload, StringComparer.Ordinal);

            foreach (var state in normal.States)
            {
                var previous = beforeNormalByName[state.Target.Processor];
                var committed = state.Attempts.Count(attempt => attempt.Outcome == OperationalAttemptOutcomes.Committed
                                                               && !previous.Attempts.Contains(attempt.Id));

                if (committed != eventsPerPhase)
                    throw new InvalidOperationException("Normal work did not record one committed attempt per event.");
            }


            var beforeOutage = await Together(host => SnapshotAsync(host, ct));
            outage.Active = true;
            var blocked = await InPhaseAsync(hosts, "shared_outage", async () =>
            {
                return await Together(async host =>
                {
                    var cursor = await host.Workload.PublishAsync(host.Events, 1, ct);
                    var state = await WaitAsync(host, s => s.Block is not null, ct);
                    var failed = FailedAttempt(state);

                    if (failed is not { Outcome: OperationalAttemptOutcomes.Failed, IsTransient: true } ||
                        failed.Sources.Count != 1 || failed.Sources[0].NextCursor != cursor.Value)
                        throw new InvalidOperationException("Injected outage did not block on the published source.");

                    return state;
                });
            }, ct);

            for (var i = 0; i < hosts.Count; i++)
            {
                var state = blocked.States[i];
                var failed = FailedAttempt(state);
                var failures = state.Attempts.Where(attempt => !beforeOutage[i].Attempts.Contains(attempt.Id)).ToArray();

                if (state.Checkpoint != cursorBeforeOutage[i].Value || failures.Length != state.Settings.FailureAttemptLimit ||
                    failures.Any(attempt => attempt is not { Outcome: OperationalAttemptOutcomes.Failed, IsTransient: true }
                                            || attempt.ExpectedCheckpoint != cursorBeforeOutage[i].Value
                                            || !attempt.Sources.SequenceEqual(failed.Sources)))
                    throw new InvalidOperationException("Outage retries must preserve the checkpoint, source, and retry budget.");

                await VerifyNativeAsync(hosts[i], cursorBeforeOutage[i], applied: false, ct);
            }

            var beforeRecovery = await Together(host => SnapshotAsync(host, ct));
            outage.Active = false;
            var recovery = await InPhaseAsync(hosts, "recovery", async () =>
            {
                return await Together(async host =>
                {
                    var blockedState = blocked.States[hosts.IndexOf(host)];
                    var failed = FailedAttempt(blockedState);
                    var plan = await host.QueueExactRepairAsync(failed, ct);
                    var cursor = new EventCursor(failed.Sources[0].NextCursor!);
                    var repaired = await WaitAsync(host, state => RepairFinished(state, plan.Id, cursor),
                        ct, $"repair {plan.Id} to checkpoint {cursor.Value}");
                    var receipt = repaired.Repairs.Single(record => record.PlanId == plan.Id);
                    var attempt = repaired.Attempts.Single(record => record.Id == receipt.AttemptId);

                    if (!attempt.Sources.SequenceEqual(failed.Sources) || receipt.Checkpoint != cursor.Value)
                        throw new InvalidOperationException("Committed repair receipt does not match the exact source and checkpoint.");

                    await VerifyNativeAsync(host, cursor, applied: true, ct);
                    return repaired;
                });
            }, ct);

            for (var index = 0; index < hosts.Count; index++)
            {
                var host = hosts[index];
                var state = await WaitAsync(host, s => s.OwnerSession != Guid.Empty, ct);
                var staleOwner = Guid.NewGuid();

                if (await host.RawStore.CompareExchangeAsync(state with { Revision = state.Revision + 1 },
                        state.Revision, staleOwner, ct))
                    throw new InvalidOperationException("A stale owner changed a workload document.");

                var current = await WaitAsync(host, s => s.OwnerSession == state.OwnerSession, ct);

                if (current.Attempts.Select(attempt => attempt.Id).Distinct().Count() != current.Attempts.Count)
                    throw new InvalidOperationException("Duplicate durable attempt IDs were observed.");
            }

            return new WorkloadRow(history, workloadCount, realm, seeded.Select(JournalBenchmarkStore.SerializedBytes).ToArray(),
                normal.ToResult(beforeNormal), blocked.ToResult(beforeOutage), recovery.ToResult(beforeRecovery));
        }
        finally
        {
            await Task.WhenAll(hosts.Select(host => host.DisposeAsync().AsTask()));
        }

        Task<T[]> Together<T>(Func<JournalBenchmarkHost, Task<T>> operation) => Task.WhenAll(hosts.Select(operation));
    }

    static OperationalAttempt FailedAttempt(OperationalProcessorState state) =>
        state.Attempts.Single(attempt => attempt.Id == state.Block!.AttemptId);

    static bool RepairFinished(OperationalProcessorState state, Guid planId, EventCursor cursor)
    {
        var repair = state.Repairs.SingleOrDefault(record => record.PlanId == planId);

        if (repair?.Status is OperationalRepairStatuses.Invalidated or OperationalRepairStatuses.Failed
            or OperationalRepairStatuses.Indeterminate or OperationalRepairStatuses.Resumed)
            throw new InvalidOperationException($"Repair {planId} for {state.Target.Processor} ended as {repair.Status} at checkpoint {state.Checkpoint}.");

        return repair?.Status == OperationalRepairStatuses.Committed
            && state.Block is null && state.Checkpoint == cursor.Value;
    }

    static async Task<OperationalProcessorState> WaitAsync(
        JournalBenchmarkHost host,
        Func<OperationalProcessorState, bool> reached,
        CancellationToken ct,
        string? waitingFor = null
    )
    {
        OperationalProcessorState? last = null;

        try
        {
            while (true)
            {
                last = await host.RawStore.ReadAsync(host.Workload.Target, ct);

                if (last is { } state && reached(state))
                    return state;

                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested && waitingFor is not null)
        {
            var latest = last is { Repairs.Count: > 0 } ? last.Repairs[^1] : null;
            throw new TimeoutException($"Timed out waiting for {waitingFor} on {host.Workload.Name}. "
                + $"Last checkpoint={last?.Checkpoint}, block={last?.Block?.AttemptId}, repair={latest?.Status}, revision={last?.Revision}.", error);
        }
    }

    static async Task VerifyNativeAsync(JournalBenchmarkHost host, EventCursor cursor, bool applied, CancellationToken ct)
    {
        var actual = await host.Projection.LoadCheckpointAsync(host.Workload.Identity, ct);
        var hasApplied = await host.Projection.HasAppliedAsync(host.Workload.PublishedCount, ct);

        if (actual.Cursor != cursor || hasApplied != applied)
            throw new InvalidOperationException($"Native model and checkpoint do not match expected progress for {host.Workload.Name}.");
    }

    static async Task<WorkerSnapshot> SnapshotAsync(JournalBenchmarkHost host, CancellationToken ct)
    {
        var state = await WaitAsync(host, _ => true, ct);
        return new WorkerSnapshot(state.Target.Processor, host.Workload.Passes, host.Workload.CheckpointLoads,
            host.Workload.Batches, host.MeasuredStore.Reads, host.MeasuredStore.Writes, host.MeasuredStore.RejectedWrites,
            host.MeasuredStore.MeasureStateBytes(), host.Workload.CommitLatencies,
            state.Attempts.Select(attempt => attempt.Id).ToHashSet());
    }

    static async Task<MeasuredPhase> InPhaseAsync(
        IReadOnlyList<JournalBenchmarkHost> hosts,
        string name,
        Func<Task<OperationalProcessorState[]>> operation,
        CancellationToken ct
    )
    {
        using var probes = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samples = new ConcurrentQueue<StatusSample>();
        await Task.WhenAll(hosts.Select(host => host.ReadStatusAsync(probes.Token)));
        var statusTask = StatusLoopAsync();
        var started = Stopwatch.GetTimestamp();
        OperationalProcessorState[] states;
        TimeSpan elapsed;

        try
        {
            states = await operation();
            elapsed = Stopwatch.GetElapsedTime(started);
        }
        finally
        {
            await probes.CancelAsync();

            try
            {
                await statusTask;
            }
            catch (OperationCanceledException) when (probes.IsCancellationRequested)
            {
            }
        }

        var after = await Task.WhenAll(hosts.Select(host => SnapshotAsync(host, ct)));
        return new MeasuredPhase(name, states, after, samples.ToArray(), elapsed.TotalMilliseconds);

        async Task ObserveAsync(JournalBenchmarkHost host)
        {
            var latency = await host.ReadStatusAsync(probes.Token);
            samples.Enqueue(new StatusSample(host.Workload.Name, latency));
        }

        async Task StatusLoopAsync()
        {
            using var timer = new PeriodicTimer(ProbeInterval);

            do
            {
                await Task.WhenAll(hosts.Select(ObserveAsync));
            } while (await timer.WaitForNextTickAsync(probes.Token));
        }
    }

    static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running the operational worker benchmark.");

    internal sealed record WorkerSnapshot(string Workload, int Passes, int CheckpointLoads, int Batches, int Reads,
        int Writes, int RejectedWrites, long StateBytes, double[] CommitLatencies, HashSet<Guid> Attempts);

    internal sealed record StatusSample(string Workload, double LatencyMs);

    internal sealed record MeasuredPhase(string Name, OperationalProcessorState[] States, WorkerSnapshot[] After,
        StatusSample[] Status, double ElapsedMs)
    {
        internal object ToResult(WorkerSnapshot[] before)
        {
            var samples = After.Zip(before, (current, previous) => new
            {
                current.Workload,
                passes = current.Passes - previous.Passes,
                nativeCheckpointLoads = current.CheckpointLoads - previous.CheckpointLoads,
                nativeBatches = current.Batches - previous.Batches,
                journalReads = current.Reads - previous.Reads,
                journalWrites = current.Writes - previous.Writes,
                rejectedWrites = current.RejectedWrites - previous.RejectedWrites,
                serializedJournalBytes = current.StateBytes - previous.StateBytes,
                commitLatenciesMs = current.CommitLatencies.Skip(previous.CommitLatencies.Length).ToArray(),
                newAttempts = States.Single(state => state.Target.Processor == current.Workload).Attempts
                    .Where(attempt => !previous.Attempts.Contains(attempt.Id))
                    .Select(attempt => new
                    {
                        attempt.Id,
                        attempt.Outcome,
                        attempt.StartedAt,
                        attempt.CompletedAt,
                        attempt.OwnerSession,
                        attempt.Sources,
                        attempt.ExpectedCheckpoint,
                        attempt.NextCheckpoint
                    }).ToArray()
            }).ToArray();
            var commits = samples.SelectMany(sample => sample.commitLatenciesMs).ToArray();
            var status = Status.Select(sample => sample.LatencyMs).ToArray();
            var passes = samples.Sum(sample => sample.passes);
            return new
            {
                Name,
                ElapsedMs,
                samples,
                commits,
                status = Status,
                commitLatencyMs = Summarize(commits),
                statusLatencyMs = Summarize(status),
                commitsPerSecond = commits.Length * 1000 / ElapsedMs,
                nativeCheckpointLoadsPerPass = passes == 0 ? (double?)null : (double)samples.Sum(sample => sample.nativeCheckpointLoads) / passes,
                journalOperationsPerPass = passes == 0 ? (double?)null : (double)samples.Sum(sample => sample.journalReads + sample.journalWrites) / passes,
                retryWakeups = Name == "shared_outage"
                    ? samples.Sum(sample => Math.Max(0, sample.newAttempts.Count(attempt => attempt.Outcome == OperationalAttemptOutcomes.Failed) - 1))
                    : 0
            };
        }
    }

    static object? Summarize(double[] values)
    {
        if (values.Length == 0)
            return null;

        var sorted = values.Order().ToArray();
        return new { count = sorted.Length, p50 = At(.50), p95 = At(.95), p99 = At(.99) };

        double At(double percentile) => sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    internal sealed record WorkloadRow(int History, int Workloads, string Realm, int[] SeededStateBytes,
        object Normal, object SharedOutage, object Recovery);
}
