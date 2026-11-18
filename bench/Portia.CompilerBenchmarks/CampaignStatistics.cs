namespace Cntryl.Portia.CompilerBenchmarks;

static partial class Campaign
{
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

    public static Comparison CompareP95(string scenario, (double[] Baseline, double[] Candidate)[] pairs)
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
        var allowance = Math.Max(baseline * .05, 1);
        var enough = pairs.Length >= 10 && pairs.Sum(pair => pair.Baseline.Length) >= 100
            && pairs.Sum(pair => pair.Candidate.Length) >= 100;
        var result = !enough ? "insufficient-processes-or-samples" : upper <= allowance ? "within-margin"
            : lower > allowance ? "regression" : "inconclusive";
        return new Comparison(scenario, "p95-ms", delta, lower, upper, allowance, result);
    }
}
