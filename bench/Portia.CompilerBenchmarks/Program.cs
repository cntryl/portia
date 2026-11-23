using System.Text.Json;

namespace Cntryl.Portia.CompilerBenchmarks;

static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, input) =>
        {
            input.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine(
                    "list | host | references | editor-fixture | capture-accounts | verify | run | compare | calibrate | first-load | retention | cancellation; options: --corpus core|accounts --case ID|all --size minimal|small|standard|large|captured --output PATH --pairs N --samples N --warmup N --seconds N --baseline DLL --candidate DLL --root PATH --edits N --delay-ms N");
                return 0;
            }

            var options = Parse(args.Skip(1).ToArray());
            var corpus = Get(options, "corpus", "core");
            var all = CaseSpec.All(corpus);

            if (args[0] == "list")
            {
                foreach (var spec in all)
                    Console.WriteLine(spec.Id);

                return 0;
            }


            var selected = Get(options, "case", args[0] == "verify" ? "all" : args[0] == "first-load" ? (corpus == "accounts" ? "accounts/api/cold" : "applications/all/full") : corpus == "accounts" ? "accounts/api/body" : "generators/all/body");
            var cases = selected == "all" ? all : [all.Single(spec => spec.Id == selected)];
            var size = Get(options, "size", corpus == "accounts" ? "captured" : "minimal");

            if (corpus == "accounts")
            {
                if (size != "captured")
                    throw new ArgumentException("Accounts uses the captured fixture dimensions.");
            }
            else
                _ = FixtureSize.Named(size);

            var samples = Number(options, "samples", args[0] is "worker" or "first-load" ? 1 : 10, 1, 1000);
            var warmup = Number(options, "warmup", args[0] == "first-load" ? 0 : 1, 0, 10);

            switch (args[0])
            {
                case "cancellation":
                    var cancellationReport = await CancellationProbe.RunAsync(cases.Single(), size, samples,
                        Number(options, "delay-ms", 1, 0, 1000), cancellation.Token).ConfigureAwait(false);
                    var cancellationPath = Path.GetFullPath(Get(options, "output", "artifacts/compiler/cancellation.json"));
                    Directory.CreateDirectory(Path.GetDirectoryName(cancellationPath)!);
                    await File.WriteAllTextAsync(cancellationPath, JsonSerializer.Serialize(cancellationReport, Campaign.Json), cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(cancellationPath);
                    return cancellationReport.Observations.Any(row => row.Error is not null) ? 2 : 0;
                case "retention":
                    var retention = await RetentionProbe.RunAsync(size, Number(options, "edits", 1000, 2, 1000),
                        row => Console.Error.WriteLine(JsonSerializer.Serialize(row, Campaign.Json)), cancellation.Token).ConfigureAwait(false);
                    var retentionPath = Path.GetFullPath(Get(options, "output", "artifacts/compiler/retention.json"));
                    Directory.CreateDirectory(Path.GetDirectoryName(retentionPath)!);
                    await File.WriteAllTextAsync(retentionPath, JsonSerializer.Serialize(retention, Campaign.Json), CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine(retentionPath);
                    return retention.Status is "within-budget" or "smoke-passed" ? 0 : retention.Status == "canceled" ? 1 : 2;
                case "capture-accounts":
                    Console.WriteLine(await AccountsCapture.CaptureAsync(Get(options, "root", "."),
                        Get(options, "output", "artifacts/compiler/accounts"), cancellation.Token).ConfigureAwait(false));
                    return 0;
                case "host":
                    Console.WriteLine(JsonSerializer.Serialize(HostCounters.Capture(), Campaign.Json));
                    return 0;
                case "editor-fixture":
                    await EditorFixture
                        .ExportAsync(Get(options, "output", "artifacts/compiler/editor-workspace"), cancellation.Token)
                        .ConfigureAwait(false);
                    return 0;
                case "references":
                    var referencePath = Path.GetFullPath(Get(options, "output", "artifacts/compiler/references.json"));
                    Directory.CreateDirectory(Path.GetDirectoryName(referencePath)!);
                    await File.WriteAllTextAsync(referencePath,
                        JsonSerializer.Serialize(CompilerHost.References.Select(reference => reference.Display),
                            Campaign.Json), cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(referencePath);
                    return 0;
                case "verify":
                    CompilerHost.VerifyInventory();
                    GeneratorChecks.Verify();
                    foreach (var spec in cases)
                    {
                        using var operation = await BenchmarkCase.PrepareAsync(spec, size, cancellation.Token)
                            .ConfigureAwait(false);
                        await operation.ExecuteAsync().ConfigureAwait(false);
                        var evidence = await operation.VerifyAsync().ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(new { spec.Id, evidence }, Campaign.Json));
                    }

                    return 0;
                case "worker":
                    Console.WriteLine(JsonSerializer.Serialize(await Campaign.WorkerAsync(cases.Single(), size, samples,
                            warmup, cancellation.Token,
                            sample => Console.WriteLine(
                                JsonSerializer.Serialize(new { Sample = sample }, Campaign.Json)),
                            completionAck: bool.Parse(Get(options, "completion-ack", "false")))
                        .ConfigureAwait(false), Campaign.Json));
                    return 0;
                case "run":
                case "compare":
                case "calibrate":
                case "first-load":
                    var current = typeof(Program).Assembly.Location;
                    var baseline = Get(options, "baseline", current);
                    var candidate = Get(options, "candidate", current);
                    return await Campaign.RunAsync(baseline, candidate, cases, size,
                        Get(options, "output", "artifacts/compiler"),
                        Number(options, "pairs", args[0] == "calibrate" ? 10 : 5, 1, 100), samples, warmup, Number(options, "seconds", 300, 1, 86400),
                        cancellation.Token, calibration: args[0] == "calibrate", firstLoad: args[0] == "first-load").ConfigureAwait(false);
                default:
                    throw new ArgumentException("Unknown command: " + args[0]);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed =
            new HashSet<string>(
                ["case", "size", "output", "pairs", "samples", "warmup", "seconds", "baseline", "candidate", "root", "corpus", "edits", "delay-ms", "completion-ack"],
                StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..]) ||
                i + 1 == args.Length
                || !values.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Unknown, duplicate or incomplete option: " + args[i]);
        }


        return values;
    }

    static string Get(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) ? value : fallback;

    static int Number(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
    {
        var value = int.Parse(Get(options, key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            System.Globalization.CultureInfo.InvariantCulture);
        return value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(key);
    }
}
