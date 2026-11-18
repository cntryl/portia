using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed class FixFixture : IDisposable
{
    public static readonly string[] Names =
        ["split", "rename", "namespace", "version", "json-create", "json-add", "partial", "reactor", "containers"];

    public AdhocWorkspace Workspace { get; } = new();
    public Document Document { get; private set; } = null!;
    public CodeFixProvider Provider { get; private set; } = null!;
    public Diagnostic Diagnostic { get; private set; } = null!;
    public string ExpectedKey { get; private set; } = "";
    public string Name { get; private set; } = "";
    Diagnostic[] _diagnostics = [];
    public int DiagnosticCount => _diagnostics.Length;

    public static async Task<FixFixture> CreateAsync(string name, int count = 1, bool fixAll = false,
        CancellationToken ct = default)
    {
        var fixture = new FixFixture { Name = name };
        try
        {
            var source = name switch
            {
                "split" => "namespace Bench.Core.Features.Work.Models; public record Price; public record Money;",
                "rename" => "namespace Bench.Core.Features.Work.Models; public record Price;",
                "namespace" => "namespace Old; public record Price(int Amount);",
                "version" =>
                    "using Cntryl.Portia; namespace Bench.Core.Features.Work.Models; [Discriminator(\"work.price\")] public sealed record Price : DomainEvent;",
                "json-create" or "json-add" =>
                    "using Cntryl.Portia; using Microsoft.Extensions.DependencyInjection; namespace App; [RequestRoute(\"app\", \"bench\", \"query\", \"run\")][Discriminator(\"bench.query\", 1)] public sealed record Query : IRequest, ICallable; public static class Composition { public static void Add(IServiceCollection services) => services.AddPortia().RegisterDynamicRequest<Query>(); }",
                "partial" or "reactor" =>
                    "using Cntryl.Portia; public sealed class Projection : Projector, IProjectorHandler<Changed> { public Projection() : base(null!, EventStreamPattern.ForPattern(\"events\")) { } public System.Threading.Tasks.ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, System.Threading.CancellationToken ct) => System.Threading.Tasks.ValueTask.FromResult(Result.Success); } [Discriminator(\"bench.changed\", 1)] public sealed record Changed : DomainEvent;",
                "containers" =>
                    "using Cntryl.Portia; public class Outer { public sealed partial class Projection : Projector, IProjectorHandler<Changed> { public Projection() : base(null!, EventStreamPattern.ForPattern(\"events\")) { } public System.Threading.Tasks.ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, System.Threading.CancellationToken ct) => System.Threading.Tasks.ValueTask.FromResult(Result.Success); } } [Discriminator(\"bench.changed\", 1)] public sealed record Changed : DomainEvent;",
                _ => throw new ArgumentException("Unknown code fix: " + name, nameof(name))
            };
            if (name == "reactor")
                source = source.Replace("Projection", "Reaction", StringComparison.Ordinal)
                    .Replace("Projector", "Reactor", StringComparison.Ordinal);
            fixture.Provider = name.StartsWith("json-", StringComparison.Ordinal) ? new JsonMetadataCodeFixProvider()
                : name is "partial" or "reactor" or "containers" ? new PartialProcessorCodeFixProvider()
                : new ApplicationStructureCodeFixProvider();
            fixture.ExpectedKey = name switch
            {
                "split" => "Portia.MoveApplicationType",
                "rename" => "Portia.RenameApplicationFile",
                "namespace" => "Portia.SyncApplicationNamespace",
                "version" => "Portia.ExplicitSchemaVersion",
                "json-create" => "Portia.CreateJsonContext",
                "json-add" => "json-add",
                "partial" or "reactor" => "Portia.MakeProcessorPartial",
                "containers" => "Portia.MakeContainersPartial",
                _ => throw new InvalidOperationException()
            };
            var project = fixture.Workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Bench.Core", "Bench.Core", LanguageNames.CSharp,
                parseOptions: CompilerHost.ParseOptions, compilationOptions: CompilerHost.CompilationOptions,
                metadataReferences: CompilerHost.References).WithDefaultNamespace("Bench.Core"));
            var file = name == "rename" ? "Wrong.cs" : "Price.cs";
            var document = project.AddDocument(file, SourceText.From(source), ["Features", "Work", "Models"],
                (name == "version"
                    ? "/benchmark/Bench.Common/Features/Work/Models/"
                    : "/benchmark/Bench.Core/Features/Work/Models/") + file);
            project = document.Project;
            if (fixAll)
            {
                for (var i = 1; i < count; i++)
                {
                    var copy = source.Replace("Price", "Price" + i, StringComparison.Ordinal)
                        .Replace("Money", "Money" + i, StringComparison.Ordinal)
                        .Replace("Query", "Query" + i, StringComparison.Ordinal)
                        .Replace("Composition", "Composition" + i, StringComparison.Ordinal)
                        .Replace("Changed", "Changed" + i, StringComparison.Ordinal)
                        .Replace("Projection", "Projection" + i, StringComparison.Ordinal)
                        .Replace("Outer", "Outer" + i, StringComparison.Ordinal)
                        .Replace("Reaction", "Reaction" + i, StringComparison.Ordinal)
                        .Replace("bench.changed", "bench.changed" + i, StringComparison.Ordinal)
                        .Replace("bench.query", "bench.query" + i, StringComparison.Ordinal)
                        .Replace("\"run\"", "\"run" + i + "\"", StringComparison.Ordinal);
                    var copyName = name == "rename" ? $"Wrong{i}.cs" : $"Price{i}.cs";
                    project = project.AddDocument(copyName, SourceText.From(copy), ["Features", "Work", "Models"],
                        "/benchmark/Bench.Core/Features/Work/Models/" + copyName).Project;
                }
            }

            if (name is "version" or "partial" or "reactor" or "containers")
            {
                var rootType = name == "version" ? "Bench.Core.Features.Work.Models.Price" : "Changed";
                var roots = string.Concat(Enumerable.Range(0, fixAll ? count : 1).Select(i =>
                    $"[JsonSerializable(typeof(global::{rootType}{(i == 0 ? "" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))}))]"));
                project = project.AddDocument("FixtureJsonContext.cs",
                        SourceText.From(
                            $"using Cntryl.Portia; using System.Text.Json.Serialization; [PortiaJsonContext]{roots} internal sealed partial class FixtureJsonContext : JsonSerializerContext;"))
                    .Project;
            }

            if (name == "json-add")
                project = project.AddDocument("AppJsonContext.cs",
                        SourceText.From(
                            "using Cntryl.Portia; using System.Text.Json.Serialization; namespace App; [PortiaJsonContext][JsonSerializable(typeof(int))] internal sealed partial class AppJsonContext : JsonSerializerContext;"))
                    .Project;
            if (name == "namespace")
            {
                var consumerId = ProjectId.CreateNewId();
                var solution = project.Solution.AddProject(ProjectInfo.Create(consumerId, VersionStamp.Create(),
                    "Bench.Api", "Bench.Api",
                    LanguageNames.CSharp, parseOptions: CompilerHost.ParseOptions,
                    compilationOptions: CompilerHost.CompilationOptions,
                    metadataReferences: CompilerHost.References,
                    projectReferences: [new ProjectReference(project.Id)]));
                for (var i = 0; i < count; i++)
                    solution = solution.AddDocument(DocumentId.CreateNewId(consumerId), $"Use{i}.cs",
                        SourceText.From(
                            $"namespace Bench.Api; public static class Use{i} {{ public static Old.Price Create() => new Old.Price({i}); }}"));
                project = solution.GetProject(project.Id)!;
            }

            if (!fixture.Workspace.TryApplyChanges(project.Solution))
                throw new InvalidOperationException("Cannot create code-fix workspace.");
            fixture.Document = fixture.Workspace.CurrentSolution.GetDocument(document.Id)!;
            fixture.Diagnostic = await fixture.FindDiagnosticAsync(ct).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    async Task<Diagnostic> FindDiagnosticAsync(CancellationToken ct)
    {
        var compilation = (await Document.Project.GetCompilationAsync(ct).ConfigureAwait(false))!;
        var expected = Name switch
        {
            "split" => "PORTIA200",
            "rename" => "PORTIA201",
            "namespace" => "PORTIA202",
            "version" => "PORTIA210",
            "json-create" or "json-add" => "PORTIA025",
            "partial" => "PORTIA002",
            "reactor" => "PORTIA005",
            "containers" => "PORTIA015",
            _ => throw new InvalidOperationException()
        };
        var diagnostics = Name is "split" or "rename" or "namespace" or "version"
            ? await compilation.WithAnalyzers(
                    [Name == "version" ? new ApplicationContractAnalyzer() : new ApplicationStructureAnalyzer()],
                    new AnalyzerOptions([], new FixtureOptions(Name == "version" ? "Common" : "Core")))
                .GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false)
            : CompilerHost.Driver().RunGenerators(compilation, ct).GetRunResult().Diagnostics;
        var mapped = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics.Where(item => item.Id == expected))
        {
            var target =
                Document.Project.Documents.Single(document =>
                    document.FilePath == diagnostic.Location.GetLineSpan().Path);
            var tree = (await target.GetSyntaxTreeAsync(ct).ConfigureAwait(false))!;
            mapped.Add(diagnostic.Location.IsInSource
                ? diagnostic
                : Diagnostic.Create(diagnostic.Descriptor,
                    Location.Create(tree, diagnostic.Location.SourceSpan), diagnostic.Properties));
        }

        _diagnostics = mapped.ToArray();
        return _diagnostics.FirstOrDefault(diagnostic => diagnostic.Location.GetLineSpan().Path == Document.FilePath)
               ?? throw new InvalidOperationException(
                   $"{Name} did not activate {expected}: {string.Join("\n", diagnostics)}");
    }

    public async Task<CodeAction> DiscoverAsync(CancellationToken ct = default)
    {
        var actions = new List<CodeAction>();
        await Provider.RegisterCodeFixesAsync(new CodeFixContext(Document, Diagnostic,
            (action, _) => actions.Add(action), ct)).ConfigureAwait(false);
        var keys = actions.Select(action =>
            Name == "json-add" && action.EquivalenceKey?.StartsWith("Portia.AddJsonRoot.", StringComparison.Ordinal) ==
            true
                ? "json-add"
                : action.EquivalenceKey ?? "");
        Verification.Actions([ExpectedKey], keys);
        return actions.Single();
    }

    public Task<CodeAction?> FixAllAsync(CodeAction action, FixAllScope scope, CancellationToken ct = default)
    {
        var context = new FixAllContext(Document, Provider, scope, action.EquivalenceKey,
            Provider.FixableDiagnosticIds, new RecordedDiagnostics(Document.Project.Id, _diagnostics), ct);
        return Provider.GetFixAllProvider()!.GetFixAsync(context);
    }

    public async Task VerifyAsync(Solution changed, CancellationToken ct = default)
    {
        var text = string.Join("\n", await Task.WhenAll(changed.Projects.SelectMany(project => project.Documents)
                .Select(async document => (await document.GetTextAsync(ct).ConfigureAwait(false)).ToString()))
            .ConfigureAwait(false));
        var valid = Name switch
        {
            "split" => changed.GetProject(Document.Project.Id)!.Documents.Any(document => document.Name == "Money.cs"),
            "rename" => changed.GetDocument(Document.Id)?.Name == "Price.cs" ||
                        changed.GetProject(Document.Project.Id)!.Documents.Any(document =>
                            document.Name == "Price.cs" && document.Id != Document.Id),
            "namespace" => !text.Contains("Old.Price", StringComparison.Ordinal) &&
                           text.Contains("namespace Bench.Core.Features.Work.Models", StringComparison.Ordinal),
            "version" => text.Contains("\"work.price\", version: 1", StringComparison.Ordinal),
            "json-create" or "json-add" => text.Contains("typeof(global::App.Query)", StringComparison.Ordinal),
            "partial" => text.Contains("partial class Projection", StringComparison.Ordinal),
            "reactor" => text.Contains("partial class Reaction", StringComparison.Ordinal),
            "containers" => text.Contains("partial class Outer", StringComparison.Ordinal),
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("Code fix did not perform " + Name);
        foreach (var project in changed.Projects)
        {
            var compilation = (await project.GetCompilationAsync(ct).ConfigureAwait(false))!;
            // Each project is regenerated independently, as a real consumer compiler does.
            var driver = CompilerHost.Driver().RunGenerators(compilation, ct);
            CompilerHost.CheckRun(driver, compilation);
        }

        var remaining = await RefreshAsync(changed, ct).ConfigureAwait(false);
        if (remaining.Any(diagnostic => diagnostic.Id == Diagnostic.Id))
            throw new InvalidOperationException("The action left an expected repair incomplete: " + Diagnostic.Id);
    }

    public async Task<ImmutableArray<Diagnostic>> RefreshAsync(Solution changed, CancellationToken ct)
    {
        var targetCompilation =
            (await changed.GetProject(Document.Project.Id)!.GetCompilationAsync(ct).ConfigureAwait(false))!;
        return Name.StartsWith("json-", StringComparison.Ordinal) || Name is "partial" or "reactor" or "containers"
            ? CompilerHost.Driver().RunGenerators(targetCompilation, ct).GetRunResult().Diagnostics
            : await targetCompilation.WithAnalyzers(
                    [Name == "version" ? new ApplicationContractAnalyzer() : new ApplicationStructureAnalyzer()],
                    new AnalyzerOptions([], new FixtureOptions(Name == "version" ? "Common" : "Core")))
                .GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Workspace.Dispose();

    sealed class RecordedDiagnostics(ProjectId projectId, Diagnostic[] diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                diagnostics.Where(diagnostic => diagnostic.Location.GetLineSpan().Path == document.FilePath));

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project,
            CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>([]);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project,
            CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(project.Id == projectId ? diagnostics : []);
    }
}
