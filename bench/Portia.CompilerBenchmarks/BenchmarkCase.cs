using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record CaseSpec(string Kind, string Operation, string Component = "all")
{
    static readonly string[] FixStages = ["discover", "discover-cached", "preview", "compute", "apply", "complete"];
    public string Id => Kind + "/" + Component + "/" + Operation;

    public static CaseSpec[] All(string corpus = "core") => corpus switch
    {
        "core" => Core(),
        "accounts" => [.. AccountsCapture.Projects.SelectMany(project => new[]
            { new CaseSpec("accounts", "cold", project.ToLowerInvariant()), new CaseSpec("accounts", "body", project.ToLowerInvariant()) })],
        _ => throw new ArgumentException("Corpus must be core or accounts.", nameof(corpus))
    };

    static CaseSpec[] Core() =>
    [
        .. ApplicationFixture.Edits.Select(edit => new CaseSpec("applications", edit)),
        new("applications", "full"), new("applications", "analyze"),
        .. CompilerFixture.Edits.Select(edit => new CaseSpec("generators", edit)), new("generators", "cold"),
        new("generators", "negative"),
        .. CompilerHost.Generators.Select(type => new CaseSpec("generators", "body", type.Name)),
        new("analyzers", "full"), new("analyzers", "document"),
        .. CompilerHost.Analyzers.Select(type => new CaseSpec("analyzers", "full", type.Name)),
        new("analyzers", "reactors-disabled", nameof(ComponentPracticeAnalyzer)),
        new("analyzers", "reactors-enabled", nameof(ComponentPracticeAnalyzer)),
        new("analyzers", "assignments", nameof(ScenarioObservationAnalyzer)),
        new("analyzers", "repeated-calls", nameof(ApplicationBoundaryAnalyzer)),
        .. FixFixture.Names.SelectMany(name => FixStages
            .Select(stage => new CaseSpec("fixes", stage, name))),
        .. FixFixture.Names.Where(name => name != "version").Select(name => new CaseSpec("fixes", "fix-all", name))
    ];
}

sealed record Evidence(string OutputHash, int Files, int Bytes, int Diagnostics, string Detail);

sealed record Sample(
    string Case,
    string Size,
    int Index,
    double Milliseconds,
    double CpuMilliseconds,
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    Evidence Evidence,
    double PreparationMilliseconds = 0,
    double VerificationMilliseconds = 0);

sealed partial class BenchmarkCase(Func<ValueTask> execute, Func<Task<Evidence>> verify, IDisposable? owner = null)
    : IDisposable
{
    static readonly string[] CombinedAnalyzerDiagnostics =
        ["PORTIA200", "PORTIA209", "PORTIA211", "PORTIA102", "PORTIA107", "PORTIA108", "PORTIA033"];

    int _executed;
    bool _completed;

    public async ValueTask ExecuteAsync()
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
            throw new InvalidOperationException(
                "An independent benchmark case may execute only once. Prepare fresh state.");

        await execute().ConfigureAwait(false);
        _completed = true;
    }

    public Task<Evidence> VerifyAsync() => _completed
        ? verify()
        : throw new InvalidOperationException("Cannot verify an incomplete operation.");

    public void Dispose() => owner?.Dispose();

    public static async Task<BenchmarkCase> PrepareAsync(CaseSpec spec, string size, CancellationToken ct = default) =>
        spec.Kind switch
        {
            "accounts" => PrepareAccounts(spec, ct),
            "applications" => PrepareApplication(spec, size, ct),
            "generators" => PrepareGenerators(spec, size, ct),
            "analyzers" => PrepareAnalyzers(spec, size, ct),
            "fixes" => await PrepareFixAsync(spec, size, ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown case kind: " + spec.Kind, nameof(spec))
        };

    public static async Task<Sample> MeasureAsync(CaseSpec spec, string size, int index, Action? requestCompleted = null, CancellationToken ct = default)
    {
        // Preparation owns fresh state for each sample. Verification is after the allocation and wall timers.
        var start = Stopwatch.GetTimestamp();
        using var operation = await PrepareAsync(spec, size, ct).ConfigureAwait(false);
        var preparation = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return (await MeasurePreparedAsync(operation, spec, size, index, requestCompleted).ConfigureAwait(false))
            with
        { PreparationMilliseconds = preparation };
    }

    public static async Task<Sample> MeasurePreparedAsync(
        BenchmarkCase operation,
        CaseSpec spec,
        string size,
        int index,
        Action? requestCompleted = null
    )
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        await operation.ExecuteAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        requestCompleted?.Invoke();
        var verificationStart = Stopwatch.GetTimestamp();
        var evidence = await operation.VerifyAsync().ConfigureAwait(false);
        var verification = Stopwatch.GetElapsedTime(verificationStart).TotalMilliseconds;
        return new Sample(spec.Id, size, index, elapsed, cpuMilliseconds, bytes,
            counts[0] - collections[0], counts[1] - collections[1], counts[2] - collections[2], evidence, 0, verification);
    }

    static BenchmarkCase PrepareGenerators(CaseSpec spec, string size, CancellationToken ct)
    {
        var fixture = CompilerFixture.Create(size, negative: spec.Operation == "negative");
        var edit = spec.Operation is "cold" or "negative" ? "unchanged" : spec.Operation;
        var input = fixture.Edit(edit).Compilation;
        var original = CompilerHost.Driver(component: spec.Component);

        if (spec.Operation != "cold")
            original = original.RunGenerators(fixture.Compilation, ct);

        var driver = original;
        return new BenchmarkCase(() =>
        {
            if (spec.Operation == "cold")
                driver = CompilerHost.Driver(component: spec.Component);

            driver = driver.RunGenerators(input, ct);
            return ValueTask.CompletedTask;
        }, () =>
        {
            CompilerHost.CheckRun(driver, input, compile: spec.Component == "all" && edit != "incomplete");
            var sources = CompilerHost.Sources(driver);
            var clean = CompilerHost.Driver(component: spec.Component).RunGenerators(input, ct);
            var cleanSources = CompilerHost.Sources(clean);
            Verification.Changes(cleanSources, sources, []);

            if (spec.Component == "all" && spec.Operation != "negative")
            {
                var emitting = CompilerHost.Generators.Where(type => type != typeof(JsonMetadataDiagnosticGenerator)
                                                                     && type !=
                                                                     typeof(RequestShapeDiagnosticsGenerator))
                    .Select(type => type.Name);
                Verification.Coverage(emitting, sources.Keys.Select(key => key.Split('/')[0]).Distinct()
                    .Where(name => name != "JsonSourceGenerator"));
            }


            if (spec.Operation is not ("cold" or "negative"))
            {
                var before = CompilerHost.Sources(original);
                var changed = before.Keys.Union(sources.Keys).Where(key => !before.TryGetValue(key, out var left)
                                                                           || !sources.TryGetValue(key,
                                                                               out var right) || left != right)
                    .ToArray();

                if (edit is "unchanged" or "body" or "incomplete")
                    Verification.Changes(before, sources, []);
                else if (spec.Component == "all" && changed.Length == 0)
                    throw new InvalidOperationException("Edit failed to invalidate outputs: " + edit);

                if (edit == "metadata" && spec.Component == "all" && !sources.Any(pair =>
                        pair.Key.StartsWith("JsonRootMarkerGenerator/", StringComparison.Ordinal) &&
                        pair.Value.Contains("units", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Nested contract description was not emitted.");

                if (spec.Component == "all" && edit is "route" or "metadata" or "location" or "event-add")
                {
                    string[] expected = edit switch
                    {
                        "route" => [nameof(RequestHttpBindingGenerator)],
                        "metadata" => [nameof(JsonRootMarkerGenerator), nameof(RegistrationCallInterceptorGenerator)],
                        "location" => [nameof(RequestHttpBindingGenerator)],
                        _ =>
                        [
                            nameof(DomainEventCatalogGenerator), nameof(JsonRootMarkerGenerator),
                            nameof(RegistrationCallInterceptorGenerator), "JsonSourceGenerator"
                        ]
                    };
                    Verification.Coverage(expected, changed.Select(key => key.Split('/')[0]).Distinct());
                }
            }

            return Task.FromResult(Snapshot(sources, driver.GetRunResult().Diagnostics.Length,
                "fresh compilation; shared metadata references; " + (spec.Operation == "cold"
                                                                      ? "new driver"
                                                                      : "populated driver")
                                                                  + "; warmup/static state recorded at worker level; clean-generation parity; combined consumer compilation"));
        });
    }

    static BenchmarkCase PrepareAnalyzers(CaseSpec spec, string size, CancellationToken ct)
    {
        var allocationWorkload = spec.Operation is not ("full" or "document");
        var fixture = CompilerFixture.Create(allocationWorkload ? "minimal" : size);
        var generator = CompilerHost.Driver().RunGenerators(fixture.Compilation, ct);
        var generated = fixture.Compilation.AddSyntaxTrees(generator.GetRunResult().GeneratedTrees);
        var role = spec.Component == nameof(ApplicationContractAnalyzer) ? "Common" : "Core";
        var control = CSharpSyntaxTree.ParseText("""
                                                 using System; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia; using Bench.Core.Features.Work.Commands;
                                                 namespace Bench.Core.Features.Work.Models;
                                                 public sealed record Unclassified : IRequest; public record Extra; public record Behavior { public int Compute() => 1; }
                                                 public sealed class ServiceAggregate(Uuid id, IServiceProvider services) : Aggregate(id, new EventStreamAddress("r", "a", id.ToString()));
                                                 public sealed record ReplayEvent : DomainEvent;
                                                 public sealed class ReplayAggregate : Aggregate
                                                 {
                                                     public ReplayAggregate(Uuid id) : base(id, new EventStreamAddress("r", "replay", id.ToString()))
                                                     {
                                                         On<ReplayEvent>(_ => throw new InvalidOperationException());
                                                     }
                                                 }
                                                 public sealed class StubHandler : IRequestHandler<StubRequest> { public ValueTask<Result> HandleAsync(IRequestContext<StubRequest> context, CancellationToken ct) => throw new NotImplementedException(); }
                                                 public static class Observation { public static void Run(IServiceProvider services) { Cntryl.Portia.Testing.RequestScenario.For(services).When(new StubRequest()); } }
                                                 """, CompilerHost.ParseOptions,
            "/benchmark/Bench." + role + "/Features/Work/Models/Controls.cs", cancellationToken: ct);
        var command = CSharpSyntaxTree.ParseText(
            "using Cntryl.Portia; namespace Bench.Core.Features.Work.Commands; public sealed record StubRequest : IRequest;",
            CompilerHost.ParseOptions, "/benchmark/Bench.Core/Features/Work/Commands/StubRequest.cs",
            cancellationToken: ct);
        var compilation = generated.AddSyntaxTrees(control, command);

        if (allocationWorkload)
            compilation = AddAllocationWorkload(compilation, spec.Operation, size, ct);

        var analyzers = CompilerHost.CreateAnalyzers(spec.Component);
        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([], new FixtureOptions(role, requireRequestActor: true)),
            null, concurrentAnalysis: spec.Component == "all", logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false);
        var driver = compilation.WithAnalyzers([.. analyzers], options);
        var contractDriver = spec.Component == "all"
            ? compilation.WithAnalyzers([new ApplicationContractAnalyzer()],
                new AnalyzerOptions([], new FixtureOptions("Common")))
            : null;
        ImmutableArray<Diagnostic> diagnostics = [];
        return new BenchmarkCase(async () =>
        {
            diagnostics = spec.Operation == "document"
                ? await driver.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(control), null, ct)
                    .ConfigureAwait(false)
                : await driver.GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false);

            if (contractDriver is not null)
                diagnostics = diagnostics.AddRange(spec.Operation == "document"
                    ? await contractDriver
                        .GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(control), null, ct)
                        .ConfigureAwait(false)
                    : await contractDriver.GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false));
        }, async () =>
        {
            if (diagnostics.Any(item => item.Id == "AD0001"))
                throw new InvalidOperationException(string.Join("\n", diagnostics));

            var expected = spec.Component switch
            {
                nameof(ApplicationContractAnalyzer) => "PORTIA209",
                nameof(ApplicationBoundaryAnalyzer) => "PORTIA211",
                nameof(RequestActorAnalyzer) => "PORTIA033",
                nameof(ComponentPracticeAnalyzer) => "PORTIA102",
                nameof(ScenarioObservationAnalyzer) => "PORTIA107",
                nameof(AggregateOnHandlerAnalyzer) => "PORTIA108",
                _ => "PORTIA200"
            };

            if (!diagnostics.Any(item => item.Id == expected))
                throw new InvalidOperationException(spec.Id + " did not activate " + expected);

            if (spec.Operation.StartsWith("reactors-", StringComparison.Ordinal)
                && diagnostics.Count(item => item.Id == "PORTIA109") != (spec.Operation == "reactors-enabled" ? 1 : 0))
                throw new InvalidOperationException(spec.Id + " returned an unexpected split-reaction count.");

            if (spec.Operation == "repeated-calls" && diagnostics.Count(item => item.Id == "PORTIA212") != 1)
                throw new InvalidOperationException(spec.Id + " did not report the query entry call.");

            var repeat = compilation.WithAnalyzers([.. CompilerHost.CreateAnalyzers(spec.Component)], options);
            var comparison = spec.Operation == "document"
                ? await repeat.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(control), null, ct)
                    .ConfigureAwait(false)
                : await repeat.GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false);

            if (spec.Component == "all")
            {
                var repeatContract = compilation.WithAnalyzers([new ApplicationContractAnalyzer()],
                    new AnalyzerOptions([], new FixtureOptions("Common")));
                comparison = comparison.AddRange(spec.Operation == "document"
                    ? await repeatContract
                        .GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(control), null, ct)
                        .ConfigureAwait(false)
                    : await repeatContract.GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false));

                foreach (var id in CombinedAnalyzerDiagnostics)
                    if (!diagnostics.Any(item => item.Id == id))
                        throw new InvalidOperationException("Combined analyzer workload did not activate " + id);
            }


            Verification.Coverage(DiagnosticKeys(comparison), DiagnosticKeys(diagnostics));
            return new Evidence(CompilerHost.Hash(string.Join("\n", DiagnosticKeys(diagnostics))), 0, 0,
                diagnostics.Length,
                "diagnostic workload; generator-prepared semantic state; " + role + "; concurrency=" +
                (spec.Component == "all"));
        });
    }

    static async Task<BenchmarkCase> PrepareFixAsync(CaseSpec spec, string size, CancellationToken ct)
    {
        var fixture = await FixFixture.CreateAsync(spec.Component, size == "minimal" ? 1 : size == "small" ? 10 : 100,
            fixAll: spec.Operation == "fix-all", ct: ct).ConfigureAwait(false);

        try
        {
            CodeAction? action = null;
            ImmutableArray<CodeActionOperation> operations = [];
            Solution? changed = null;

            if (spec.Operation is not ("discover" or "complete"))
                action = await fixture.DiscoverAsync(ct).ConfigureAwait(false);


            if (spec.Operation == "apply")
                operations = await action!.GetOperationsAsync(ct).ConfigureAwait(false);

            return new BenchmarkCase(async () =>
            {
                switch (spec.Operation)
                {
                    case "discover":
                    case "discover-cached":
                        action = await fixture.DiscoverAsync(ct).ConfigureAwait(false);
                        break;
                    case "preview":
                        operations = await action!.GetPreviewOperationsAsync(ct).ConfigureAwait(false);
                        break;
                    case "compute":
                        operations = await action!.GetOperationsAsync(ct).ConfigureAwait(false);
                        break;
                    case "fix-all":
                        action = await fixture.FixAllAsync(action!, FixAllScope.Project, ct).ConfigureAwait(false)
                                 ?? throw new InvalidOperationException("Fix All was not offered.");
                        operations = await action.GetOperationsAsync(ct).ConfigureAwait(false);
                        break;
                    case "complete":
                        action = await fixture.DiscoverAsync(ct).ConfigureAwait(false);
                        _ = await action.GetPreviewOperationsAsync(ct).ConfigureAwait(false);
                        operations = await action.GetOperationsAsync(ct).ConfigureAwait(false);
                        goto case "apply";
                    case "apply":
                        foreach (var operation in operations)
                            operation.Apply(fixture.Workspace, ct);
                        changed = fixture.Workspace.CurrentSolution;
                        if (spec.Operation == "complete")
                            _ = await fixture.RefreshAsync(changed, ct).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentException("Unknown code fix stage: " + spec.Operation, nameof(spec));
                }
            }, async () =>
            {
                if (spec.Operation is "discover" or "discover-cached")
                    operations = await action!.GetOperationsAsync(ct).ConfigureAwait(false);

                changed ??= operations.OfType<ApplyChangesOperation>().SingleOrDefault()?.ChangedSolution
                            ?? throw new InvalidOperationException("Action produced no solution change.");
                await fixture.VerifyAsync(changed, ct).ConfigureAwait(false);
                var texts = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var project in changed.Projects)
                    foreach (var document in project.Documents)
                        texts.Add(project.Name + "/" + document.Name,
                            (await document.GetTextAsync(ct).ConfigureAwait(false)).ToString());

                return Snapshot(texts, fixture.DiagnosticCount,
                    "fresh AdhocWorkspace/project; diagnostics already bound; "
                    + (spec.Operation == "discover-cached"
                        ? "same-project discovery cache hit"
                        : "first discovery or prepared action")
                    + (spec.Operation == "complete" ? "; diagnostic refresh included; " : "; refresh outside timer; ") +
                    spec.Operation);
            }, fixture);
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    static string[] DiagnosticKeys(IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(item =>
            $"{item.Id}|{item.Severity}|{item.Location.SourceTree?.FilePath}|{item.Location.SourceSpan}|{item.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
        .Order(StringComparer.Ordinal).ToArray();

    static Evidence Snapshot(IReadOnlyDictionary<string, string> texts, int diagnostics, string detail) => new(
        CompilerHost.Hash(string.Join("\n", texts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "|" + CompilerHost.Hash(pair.Value)))), texts.Count,
        texts.Values.Sum(text => System.Text.Encoding.UTF8.GetByteCount(text)), diagnostics, detail);
}
