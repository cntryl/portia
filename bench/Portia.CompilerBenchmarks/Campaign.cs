using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record WorkerReport(
    string Format,
    string Case,
    string Size,
    int Warmup,
    string WarmupSize,
    int ProcessId,
    string FixtureHash,
    string HarnessHash,
    string Runtime,
    string Roslyn,
    string OS,
    string Architecture,
    Dictionary<string, string> Assemblies,
    Sample[] Samples);

sealed record WorkerResult(
    int Pair,
    string Side,
    string Assembly,
    double ProcessMilliseconds,
    WorkerReport? Report,
    string? Error);

sealed record Comparison(
    string Case,
    string Metric,
    double Delta,
    double Lower,
    double Upper,
    double Allowance,
    string Result);

static class Campaign
{
    public const string Format = "portia-compiler-benchmarks-2";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static async Task<WorkerReport> WorkerAsync(CaseSpec spec, string size, int samples, int warmup,
        CancellationToken ct,
        Action<Sample>? completed = null)
    {
        CompilerHost.VerifyInventory();
        for (var i = 0; i < warmup; i++)
            _ = await BenchmarkCase.MeasureAsync(spec, "minimal", -1, ct).ConfigureAwait(false);
        var rows = new List<Sample>();
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = await BenchmarkCase.MeasureAsync(spec, size, i, ct).ConfigureAwait(false);
            rows.Add(row);
            completed?.Invoke(row);
        }

        var fixture = CompilerFixture.Create(size);
        var fixtureHash = CompilerHost.Hash(string.Join("\n",
            fixture.Compilation.SyntaxTrees.Select(tree => tree.FilePath + "|" + tree.GetText())));
        var assemblies = CompilerHost.Generators.Concat(CompilerHost.Analyzers).Concat(CompilerHost.CodeFixes)
            .Select(type => type.Assembly).Append(typeof(Aggregate).Assembly).Append(typeof(PortiaBuilder).Assembly)
            .Append(typeof(Microsoft.CodeAnalysis.Compilation).Assembly).Distinct()
            .ToDictionary(assembly => assembly.GetName().Name!, assembly => CompilerHost.FileHash(assembly.Location),
                StringComparer.Ordinal);
        assemblies.Add("System.Text.Json.SourceGeneration", CompilerHost.FileHash(CompilerHost.JsonGeneratorPath));
        assemblies.Add("harness-il", CompilerHost.Hash(string.Join("\n", typeof(Campaign).Assembly.GetTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal).SelectMany(type => type
                .GetMethods(BindingFlags.Public
                            | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                            BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                                BindingFlags.Static | BindingFlags.Instance))
                .OrderBy(method => method.MetadataToken).Select(method => type.FullName + "|" + method + "|"
                                                                          + Convert.ToHexString(method.GetMethodBody()
                                                                              ?.GetILAsByteArray() ?? []))))));
        assemblies.Add("fixture-references", CompilerHost.Hash(string.Join("\n", CompilerHost.References
            .Select(reference => CompilerHost.FileHash(reference.Display!)).Order(StringComparer.Ordinal))));
        return new WorkerReport(Format, spec.Id, size, warmup, warmup == 0 ? "none" : "minimal", Environment.ProcessId,
            fixtureHash,
            CompilerHost.FileHash(typeof(Campaign).Assembly.Location), RuntimeInformation.FrameworkDescription,
            typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version!.ToString(),
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), assemblies,
            rows.ToArray());
    }

    public static async Task<WorkerResult> LaunchAsync(string assembly, CaseSpec spec, string size, int samples,
        int warmup,
        int pair, string side, TimeSpan timeout, CancellationToken ct, Func<string, Task>? progress = null)
    {
        var start = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     Path.GetFullPath(assembly), "worker", "--case", spec.Id, "--size", size,
                     "--samples", samples.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--warmup", warmup.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("Could not launch worker.");
        var stdout = ReadWorkerAsync(process.StandardOutput, progress);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new WorkerResult(pair, side, assembly, watch.Elapsed.TotalMilliseconds, null,
                    "exit=" + process.ExitCode + "; " + error + output);
            var report = JsonSerializer.Deserialize<WorkerReport>(output, Json) ??
                         throw new InvalidOperationException("Empty worker report.");
            if (report.Case != spec.Id || report.Size != size || report.Samples.Length != samples)
                throw new InvalidOperationException("Worker did not report the requested case and sample count.");
            return new WorkerResult(pair, side, assembly, watch.Elapsed.TotalMilliseconds, report, null);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return new WorkerResult(pair, side, assembly, watch.Elapsed.TotalMilliseconds, null,
                ct.IsCancellationRequested ? "campaign canceled" : "worker timed out");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new WorkerResult(pair, side, assembly, watch.Elapsed.TotalMilliseconds, null, error.Message);
        }
    }

    public static async Task<int> RunAsync(string baseline, string candidate, CaseSpec[] cases, string size,
        string directory, int pairs, int samples, int warmup, int seconds, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N"));
        await using var stream = new FileStream(path + ".jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var results = new List<WorkerResult>();
        var complete = true;
        foreach (var spec in cases)
        {
            for (var pair = 0; pair < pairs; pair++)
            {
                foreach (var side in pair % 2 == 0
                             ? new[] { "baseline", "candidate" }
                             : new[] { "candidate", "baseline" })
                {
                    if (deadline.IsCancellationRequested)
                    {
                        complete = false;
                        break;
                    }

                    var result = await LaunchAsync(side == "baseline" ? baseline : candidate, spec, size, samples,
                        warmup, pair, side, TimeSpan.FromSeconds(seconds), deadline.Token, async line =>
                        {
                            await writer
                                .WriteLineAsync(
                                    JsonSerializer.Serialize(new { pair, side, sample = JsonElement.Parse(line) },
                                        Json)).ConfigureAwait(false);
                            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            stream.Flush(flushToDisk: true);
                        }).ConfigureAwait(false);
                    results.Add(result);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result, Json)).ConfigureAwait(false);
                    Console.Error.WriteLine(
                        $"{spec.Id} pair {pair + 1}/{pairs} {side}: {(result.Error is null ? "verified" : result.Error)}");
                    if (result.Error is not null)
                    {
                        complete = false;
                        break;
                    }
                }

                if (!complete)
                    break;
            }

            if (!complete)
                break;
        }

        Comparison[] comparisons = [];
        string? comparisonError = null;
        if (complete)
        {
            try
            {
                comparisons = Compare(results);
            }
            catch (InvalidOperationException error)
            {
                complete = false;
                comparisonError = error.Message;
            }
        }

        var report = new
        {
            format = Format,
            status = complete ? "diagnostic-comparison" : "incomplete",
            qualification = "pending controlled-host acceptance, A/A calibration, and complete design coverage",
            baseline = Path.GetFullPath(baseline),
            candidate = Path.GetFullPath(candidate),
            pairs,
            samples,
            warmup,
            size,
            comparisons,
            comparisonError,
            observations = results.Where(result => result.Report is not null)
                .GroupBy(result => (result.Report!.Case, result.Side))
                .Select(group => new
                {
                    group.Key.Case,
                    group.Key.Side,
                    processes = group.Count(),
                    operations = group.Sum(result => result.Report!.Samples.Length),
                    medianMilliseconds =
                        Quantile(group.SelectMany(result => result.Report!.Samples.Select(row => row.Milliseconds)),
                            .5),
                    p95Milliseconds =
                        Quantile(group.SelectMany(result => result.Report!.Samples.Select(row => row.Milliseconds)),
                            .95),
                    p95IsDiagnosticOnly = true
                }).ToArray(),
            raw = Path.GetFileName(path + ".jsonl")
        };
        await File.WriteAllTextAsync(path + ".summary.json", JsonSerializer.Serialize(report, PrettyJson),
            CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(path + ".summary.json");
        return complete ? 0 : 1;
    }

    static async Task<string> ReadWorkerAsync(StreamReader reader, Func<string, Task>? progress)
    {
        var report = "";
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("{\"Sample\":", StringComparison.Ordinal))
            {
                if (progress is not null)
                    await progress(line).ConfigureAwait(false);
            }
            else
                report = line;
        }

        return report;
    }

    public static Comparison[] Compare(IReadOnlyList<WorkerResult> results)
    {
        var comparisons = new List<Comparison>();
        foreach (var group in results.GroupBy(result =>
                     result.Report?.Case ?? throw new InvalidOperationException("Cannot compare failed workers.")))
        {
            var blocks = group.GroupBy(result => result.Pair).Select(pair =>
            {
                var baseline = pair.Single(item => item.Side == "baseline").Report!;
                var candidate = pair.Single(item => item.Side == "candidate").Report!;
                foreach (var report in new[] { baseline, candidate })
                    if (report.Samples.Where((row, index) =>
                            row.Index != index || row.Case != report.Case || row.Size != report.Size
                            || row.AllocatedBytes < 0 || !double.IsFinite(row.Milliseconds) || row.Milliseconds < 0)
                        .Any())
                        throw new InvalidOperationException(
                            "Worker report contains missing, invalid or misidentified samples.");
                if (baseline.Format != candidate.Format || baseline.Format != Format ||
                    baseline.FixtureHash != candidate.FixtureHash
                    || baseline.Warmup != candidate.Warmup || baseline.WarmupSize != candidate.WarmupSize
                    || baseline.Runtime != candidate.Runtime || baseline.Roslyn != candidate.Roslyn ||
                    baseline.OS != candidate.OS
                    || baseline.Architecture != candidate.Architecture || baseline.Size != candidate.Size
                    || baseline.Samples.Length != candidate.Samples.Length || baseline.Samples.Length == 0
                    || baseline.Assemblies["harness-il"] != candidate.Assemblies["harness-il"]
                    || baseline.Assemblies["fixture-references"] != candidate.Assemblies["fixture-references"]
                    || baseline.Assemblies["System.Text.Json.SourceGeneration"] !=
                    candidate.Assemblies["System.Text.Json.SourceGeneration"])
                    throw new InvalidOperationException(
                        "Baseline and candidate have incompatible workload or host manifests.");
                Verification.Coverage(baseline.Samples.Select(row => row.Evidence.OutputHash),
                    candidate.Samples.Select(row => row.Evidence.OutputHash));
                return (Baseline: baseline, Candidate: candidate);
            }).ToArray();
            comparisons.Add(CompareMetric(group.Key, "median-ms", blocks.Select(block =>
                    (Quantile(block.Baseline.Samples.Select(row => row.Milliseconds), .5),
                        Quantile(block.Candidate.Samples.Select(row => row.Milliseconds), .5))).ToArray(),
                group.Key.EndsWith("/cold", StringComparison.Ordinal) ? 10 : 1));
            comparisons.Add(CompareMetric(group.Key, "allocated-bytes", blocks.Select(block =>
                    (Quantile(block.Baseline.Samples.Select(row => (double)row.AllocatedBytes), .5),
                        Quantile(block.Candidate.Samples.Select(row => (double)row.AllocatedBytes), .5))).ToArray(),
                65536));
        }

        return comparisons.ToArray();
    }

    public static Comparison CompareMetric(string scenario, string metric, (double Baseline, double Candidate)[] pairs,
        double absolute)
    {
        if (pairs.Length == 0 || pairs.Any(pair => !double.IsFinite(pair.Baseline) || !double.IsFinite(pair.Candidate)))
            throw new InvalidOperationException("Comparison requires finite complete pairs.");
        var deltas = pairs.Select(pair => pair.Candidate - pair.Baseline).ToArray();
        var random = new Random(175);
        var bootstrap = new double[2000];
        for (var i = 0; i < bootstrap.Length; i++)
            bootstrap[i] = Quantile(Enumerable.Range(0, pairs.Length).Select(_ => deltas[random.Next(deltas.Length)]),
                .5);
        var lower = Quantile(bootstrap, .025);
        var upper = Quantile(bootstrap, .975);
        var allowance = Math.Max(Quantile(pairs.Select(pair => pair.Baseline), .5) * .05, absolute);
        var decision = pairs.Length < 5 ? "insufficient-pairs" :
            upper <= allowance ? "within-margin" :
            lower > allowance ? "regression" : "inconclusive";
        return new Comparison(scenario, metric, Quantile(deltas, .5), lower, upper, allowance, decision);
    }

    public static double Quantile(IEnumerable<double> source, double fraction)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0)
            throw new InvalidOperationException("No observations.");
        return values[Math.Clamp((int)Math.Ceiling(fraction * values.Length) - 1, 0, values.Length - 1)];
    }
}
