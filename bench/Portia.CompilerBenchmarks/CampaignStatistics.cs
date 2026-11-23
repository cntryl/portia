namespace Cntryl.Portia.CompilerBenchmarks;

static partial class Campaign
{
    public static Comparison[] CompareFirstRequests(IReadOnlyList<WorkerResult> results)
    {
        return results.GroupBy(result => result.Report?.Case ?? throw new InvalidOperationException("Missing worker report."))
            .SelectMany(group =>
            {
                var pairs = group.GroupBy(result => result.Pair).Select(pair =>
                {
                    var baseline = pair.Single(row => row.Side == "baseline");
                    var candidate = pair.Single(row => row.Side == "candidate");

                    if (baseline.Report!.Warmup != 0 || candidate.Report!.Warmup != 0
                        || baseline.Report.Samples.Length != 1 || candidate.Report.Samples.Length != 1
                        || baseline.FirstRequestMilliseconds is not { } first || candidate.FirstRequestMilliseconds is not { } second
                        || !double.IsFinite(first) || !double.IsFinite(second) || first <= 0 || second <= 0)
                        throw new InvalidOperationException("First-load report has no valid cold request completion boundary.");

                    return (first, second);
                }).ToArray();
                var median = CompareMetric(group.Key, "first-request-median-ms", pairs, 10);
                var p95 = CompareP95(group.Key, pairs.Select(pair => (new[] { pair.first }, new[] { pair.second })).ToArray(), 10, 100)
                    with
                { Metric = "first-request-p95-ms" };

                if (pairs.Length < 100)
                    p95 = p95 with { Result = "insufficient-cold-processes" };

                return new[] { median, p95 };
            }).ToArray();
    }

    static void ValidateManifests(IEnumerable<WorkerResult> results)
    {
        foreach (var side in results.GroupBy(result => result.Side))
        {
            var first = side.First().Report ?? throw new InvalidOperationException("Missing worker report.");

            foreach (var worker in side)
            {
                var report = worker.Report ?? throw new InvalidOperationException("Missing worker report.");

                if (report.Format != first.Format || report.Size != first.Size || report.FixtureHash != first.FixtureHash
                    || report.Warmup != first.Warmup || report.WarmupSize != first.WarmupSize
                    || report.HarnessHash != first.HarnessHash || report.Runtime != first.Runtime || report.OS != first.OS
                    || report.Architecture != first.Architecture || report.Roslyn != first.Roslyn
                    || report.Samples.Length != first.Samples.Length
                    || !report.Assemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .SequenceEqual(first.Assemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Campaign changed its fixture, warmup, host, or revision between process pairs.");
            }
        }
    }

    public static Comparison CompareP95(string scenario, (double[] Baseline, double[] Candidate)[] pairs, double absolute = 1, int minimumProcesses = 10)
    {
        if (pairs.Length == 0 || pairs.Any(pair => pair.Baseline.Length == 0 || pair.Candidate.Length == 0
            || pair.Baseline.Concat(pair.Candidate).Any(value => !double.IsFinite(value) || value < 0)))
            throw new InvalidOperationException("P95 comparison requires finite complete process pairs.");

        var baseline = Quantile(pairs.SelectMany(pair => pair.Baseline), .95);
        var delta = Quantile(pairs.SelectMany(pair => pair.Candidate), .95) - baseline;
        var distribution = new double[2000];
        var random = new Random(175);

        for (var i = 0; i < distribution.Length; i++)
        {
            var selected = Enumerable.Range(0, pairs.Length).Select(_ => pairs[random.Next(pairs.Length)]).ToArray();
            distribution[i] = Quantile(selected.SelectMany(pair => pair.Candidate), .95)
                - Quantile(selected.SelectMany(pair => pair.Baseline), .95);
        }

        var lower = Quantile(distribution, .025);
        var upper = Quantile(distribution, .975);
        var allowance = Math.Max(baseline * .05, absolute);
        var enough = pairs.Length >= minimumProcesses && pairs.Sum(pair => pair.Baseline.Length) >= 100
            && pairs.Sum(pair => pair.Candidate.Length) >= 100;
        var result = !enough ? "insufficient-processes-or-samples" : upper <= allowance ? "within-margin"
            : lower > allowance ? "regression" : "inconclusive";
        return new Comparison(scenario, "p95-ms", delta, lower, upper, allowance, result);
    }
}
