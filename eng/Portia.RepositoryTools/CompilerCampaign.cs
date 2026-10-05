using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Cntryl.Portia.RepositoryTools;

static class CompilerCampaign
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static int ResolveJsonGenerator(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: resolve-json-generator <resolved-analyzers.json>");

        using var document = JsonDocument.Parse(File.ReadAllText(args[1]));
        var analyzers = document.RootElement.GetProperty("Items").GetProperty("Analyzer");
        var matches = analyzers.EnumerateArray()
            .Select(analyzer => analyzer.GetProperty("Identity").GetString())
            .Where(identity => identity?.Replace('\\', '/').EndsWith(
                "/System.Text.Json.SourceGeneration.dll", StringComparison.Ordinal) == true)
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected one System.Text.Json source generator, found {matches.Length}.");

        Console.WriteLine(matches[0]);
        return 0;
    }

    internal static async Task<int> BuildCampaignAsync(string[] args)
    {
        if (args.Length is < 2 or > 3)
            throw new ArgumentException("Usage: compiler-build-campaign <evidence-directory> [repetitions]");

        var output = Path.GetFullPath(args[1]);
        var repetitions = args.Length == 3 ? ParsePositiveInt(args[2], "repetitions") : 5;
        Directory.CreateDirectory(output);
        var fixtures = Path.Combine(output, "fixtures");
        var projects = Directory.Exists(fixtures)
            ? Directory.EnumerateDirectories(fixtures)
                .Select(directory => (Directory: directory, Project: Path.Combine(directory, "CompilerConsumer.csproj")))
                .Where(entry => File.Exists(entry.Project))
                .OrderBy(entry => Path.GetFileName(entry.Directory), StringComparer.Ordinal)
                .ToArray()
            : [];
        var rows = new List<BuildSample>();

        foreach (var entry in projects)
        {
            var workload = Path.GetFileName(entry.Directory);
            var setupLog = Path.Combine(output, $"{workload}-build-setup.log");
            await File.WriteAllTextAsync(setupLog, string.Empty).ConfigureAwait(false);
            await RunLoggedAsync(setupLog, "restore", entry.Project).ConfigureAwait(false);
            await RunLoggedAsync(setupLog, "build", entry.Project, "-c", "Release", "--no-restore", "-m:1",
                "/nodeReuse:false", "-p:UseSharedCompilation=false").ConfigureAwait(false);

            foreach (var mode in new[] { "cold-compiler", "unchanged-build" })
            {
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    var command = new List<string>
                    {
                        "build", entry.Project, "-c", "Release", "--no-restore", "-m:1", "/nodeReuse:false",
                        "-p:UseSharedCompilation=false", "-p:BuildProjectReferences=false"
                    };
                    if (mode == "cold-compiler")
                        command.Add("--no-incremental");

                    var logPath = Path.Combine(output, $"{workload}-{mode}-{repetition}.log");
                    await File.WriteAllTextAsync(logPath, string.Empty).ConfigureAwait(false);
                    var stopwatch = Stopwatch.StartNew();
                    await RunLoggedAsync(logPath, command.ToArray()).ConfigureAwait(false);
                    stopwatch.Stop();
                    rows.Add(new BuildSample(workload, mode, repetition, stopwatch.Elapsed.TotalMilliseconds,
                        ["dotnet", .. command], true));
                    await WriteJsonAsync(Path.Combine(output, "build-raw.json"), rows).ConfigureAwait(false);
                }
            }

            Console.WriteLine($"{workload} actual compiler/build controls passed");
        }

        var summary = new List<BuildSummary>();
        foreach (var workload in rows.Select(row => row.Workload).Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            foreach (var mode in new[] { "cold-compiler", "unchanged-build" })
            {
                var values = rows.Where(row => row.Workload == workload && row.Mode == mode)
                    .Select(row => row.Milliseconds)
                    .Order()
                    .ToArray();
                if (values.Length == 0)
                    throw new InvalidOperationException($"No build measurements were recorded for {workload} ({mode}).");

                summary.Add(new BuildSummary(workload, mode, values.Length, Median(values), values[^1],
                    "MSBuild invocation with prebuilt framework references; fresh compiler process for cold builds; restore excluded"));
            }
        }

        await WriteJsonAsync(Path.Combine(output, "build-summary.json"), summary).ConfigureAwait(false);
        return 0;
    }

    internal static int MergeCampaign(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: compiler-merge-campaign <evidence-directory> <repetitions>");

        var output = Path.GetFullPath(args[1]);
        var repetitions = ParsePositiveInt(args[2], "repetitions");
        var profilesRoot = Path.Combine(output, "profiles");
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.EnumerateDirectories(profilesRoot).Order(StringComparer.Ordinal).ToArray()
            : [];
        if (profiles.Length == 0)
            throw new InvalidOperationException("No compiler workload matched the declared filter.");

        var jsonFiles = new[] { "raw.json", "controls.json", "summary.json", "build-raw.json", "build-summary.json" };
        foreach (var fileName in jsonFiles)
        {
            var combined = new JsonArray();
            foreach (var profile in profiles)
            {
                var path = Path.Combine(profile, fileName);
                var values = JsonNode.Parse(File.ReadAllText(path)) as JsonArray
                             ?? throw new InvalidDataException($"Expected a JSON array in {path}.");
                if (fileName == "raw.json")
                    ValidateRepetitions(profile, values, repetitions);
                foreach (var value in values)
                    combined.Add(value?.DeepClone());
            }

            WriteJson(Path.Combine(output, fileName), combined);
        }

        MergeCsv(profiles, Path.Combine(output, "raw.csv"));
        var profileEnvironment = new JsonObject();
        foreach (var profile in profiles)
        {
            var environmentPath = Path.Combine(profile, "environment.json");
            profileEnvironment.Add(Path.GetFileName(profile), JsonNode.Parse(File.ReadAllText(environmentPath))
                ?? throw new InvalidDataException($"Expected a JSON value in {environmentPath}."));
        }

        var environment = new JsonObject
        {
            ["sourceCommit"] = File.ReadAllText(Path.Combine(output, "source-commit.txt")).Trim(),
            ["method"] = "One fresh process per workload; full controls and JIT warmup before sampling; atomic checkpoint after each repetition outside operation timings.",
            ["profiles"] = profileEnvironment
        };
        WriteJson(Path.Combine(output, "environment.json"), environment);
        Console.WriteLine($"Validated {profiles.Length} complete compiler workloads with {repetitions} samples per measurement group.");
        return 0;
    }

    static async Task RunLoggedAsync(string logPath, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("dotnet", arguments, Directory.GetCurrentDirectory())
            .ConfigureAwait(false);
        await File.AppendAllTextAsync(logPath, result.Output).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed; see {logPath}.");
    }

    static async Task WriteJsonAsync<T>(string path, T value)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine)
            .ConfigureAwait(false);
    }

    static void WriteJson(string path, JsonNode value) =>
        File.WriteAllText(path, value.ToJsonString(JsonOptions) + Environment.NewLine);

    static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{name} must be a positive integer.");
        return parsed;
    }

    static double Median(double[] sorted) => sorted.Length % 2 == 1
        ? sorted[sorted.Length / 2]
        : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d;

    static void ValidateRepetitions(string profile, JsonArray rows, int repetitions)
    {
        var groups = new Dictionary<(string Workload, string Edit, string Component), List<int>>();
        foreach (var row in rows)
        {
            if (row is not JsonObject item)
                throw new InvalidDataException($"Expected an object in {profile}/raw.json.");
            var key = (
                item["Workload"]?.GetValue<string>() ?? throw new InvalidDataException("Missing Workload in raw.json."),
                item["Edit"]?.GetValue<string>() ?? throw new InvalidDataException("Missing Edit in raw.json."),
                item["Component"]?.GetValue<string>() ?? throw new InvalidDataException("Missing Component in raw.json."));
            var repetition = item["Repetition"]?.GetValue<int>()
                             ?? throw new InvalidDataException("Missing Repetition in raw.json.");
            if (!groups.TryGetValue(key, out var observed))
            {
                observed = [];
                groups.Add(key, observed);
            }

            observed.Add(repetition);
        }

        foreach (var group in groups.OrderBy(pair => pair.Key.Workload, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Edit, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Component, StringComparer.Ordinal))
        {
            var observed = group.Value.Order().ToArray();
            if (observed.Length != repetitions || observed.Where((value, index) => value != index).Any())
                throw new InvalidOperationException($"Incomplete samples: {Path.GetFileName(profile)}, {group.Key.Edit}, {group.Key.Component}");
        }
    }

    static void MergeCsv(string[] profiles, string outputPath)
    {
        using var destination = new StreamWriter(outputPath, false);
        string? expectedHeader = null;
        foreach (var profile in profiles)
        {
            var path = Path.Combine(profile, "raw.csv");
            using var source = new StreamReader(path);
            var header = source.ReadLine() ?? throw new InvalidDataException($"Missing CSV header in {path}.");
            if (expectedHeader is not null && header != expectedHeader)
                throw new InvalidDataException($"CSV headers do not match in {path}.");
            if (expectedHeader is null)
            {
                expectedHeader = header;
                destination.WriteLine(header);
            }

            while (source.ReadLine() is { } line)
                destination.WriteLine(line);
        }
    }

    sealed record BuildSample(string Workload, string Mode, int Repetition, double Milliseconds,
        string[] Command, bool CompiledSuccessfully);

    sealed record BuildSummary(string Workload, string Mode, int Samples, double P50Milliseconds,
        double P95Milliseconds, string Boundary);
}
