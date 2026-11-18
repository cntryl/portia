using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record ApplicationProjectResult(string Role, CSharpCompilation User, CSharpCompilation Input,
    CSharpCompilation Output, GeneratorDriver Driver, MetadataReference Reference);

sealed record ApplicationBuild(ApplicationProjectResult[] Projects)
{
    public IReadOnlyDictionary<string, string> Sources() => Projects.SelectMany(project =>
            CompilerHost.Sources(project.Driver).Select(pair =>
                new KeyValuePair<string, string>(project.Role + "/" + pair.Key, pair.Value)))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

sealed class ApplicationFixture(Dictionary<string, CSharpCompilation> inputs)
{
    public static readonly string[] Edits = ["unchanged", "body", "metadata", "reference", "incomplete"];
    static readonly string[] Roles = ["Common", "Core", "Api", "Worker"];

    public string Identity => CompilerHost.Hash(string.Join("\n", Roles.SelectMany(role => inputs[role].SyntaxTrees
        .Select(tree => tree.FilePath + "|" + tree.GetText()))));

    public static ApplicationFixture Create(string sizeName)
    {
        var size = FixtureSize.Named(sizeName);
        var features = Math.Min(10, size.Requests);
        var files = Roles.ToDictionary(role => role, _ => new List<SyntaxTree>(), StringComparer.Ordinal);
        void Add(string role, string path, string body)
        {
            var folder = Path.GetDirectoryName(path)!.Replace('/', '.');
            var space = "Bench." + role + (folder.Length == 0 ? "" : "." + folder);
            files[role].Add(CSharpSyntaxTree.ParseText(
                "using System; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia;\nnamespace "
                + space + ";\n" + body, CompilerHost.ParseOptions, "/benchmark/Bench." + role + "/" + path,
                Encoding.UTF8));
        }
        for (var i = 0; i < size.Requests; i++)
        {
            var feature = "Feature" + i % features;
            Add("Common", $"Features/{feature}/Commands/Change{i}.cs", $$"""
                [Discriminator("feature{{i % features}}.change_{{i}}", 1)]
                [RequestRoute("app", "bench", "change", "run{{i}}")]
                public sealed record Change{{i}}([property: System.ComponentModel.Description("quantity")]
                    [property: System.ComponentModel.DataAnnotations.Range(0, 100)] int Id = 1) : IRequest, ICallable;
                """);
            Add("Core", $"Features/{feature}/Handlers/Change{i}Handler.cs", $$"""
                public sealed class Change{{i}}Handler : IRequestHandler<Bench.Common.Features.{{feature}}.Commands.Change{{i}}>
                {
                    public ValueTask<Result> HandleAsync(IRequestContext<Bench.Common.Features.{{feature}}.Commands.Change{{i}}> context, CancellationToken ct)
                        => ValueTask.FromResult(Count() > 0 ? Result.Success : RequestError.Validation("quantity"));
                    private static int Count() => 1;
                }
                """);
        }
        for (var i = 0; i < size.Events; i++)
            Add("Common", $"Features/Feature{i % features}/Events/Changed{i}.cs",
                $"[Discriminator(\"feature{i % features}.changed_{i}\", 1)] public sealed record Changed{i}(int Value) : DomainEvent;");
        for (var i = 0; i < size.Processors; i++)
        {
            var feature = "Feature" + i % features;
            var contract = $"Bench.Common.Features.{feature}.Events.Changed{i % size.Events}";
            Add("Core", $"Features/{feature}/Projectors/Projection{i}.cs", $$"""
                public sealed partial class Projection{{i}}(IProjectionStore store) : Projector(store, EventStreamPattern.ForPattern("events")), IProjectorHandler<{{contract}}>
                { public ValueTask<Result> HandleAsync(IProjectorContext<{{contract}}> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }
                """);
            Add("Core", $"Features/{feature}/Reactors/Reaction{i}.cs", $$"""
                public sealed partial class Reaction{{i}}(IProjectionCheckpointStore store) : Reactor(store, EventStreamPattern.ForPattern("events")), IReactorHandler<{{contract}}>
                { public ValueTask<Result> HandleAsync(IReactorContext<{{contract}}> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }
                """);
        }
        for (var i = 0; i < size.Unrelated; i++)
            Add("Common", $"Features/Feature{i % features}/Models/Unrelated{i}.cs",
                $"public sealed record Unrelated{i}(int Value);");
        for (var i = 0; i < size.Contexts; i++)
        {
            var context = new StringBuilder("[PortiaJsonContext]\n");
            for (var j = i; j < size.Requests; j += size.Contexts)
                context.AppendLine(CultureInfo.InvariantCulture,
                    $"[System.Text.Json.Serialization.JsonSerializable(typeof(Bench.Common.Features.Feature{j % features}.Commands.Change{j}))]");
            for (var j = i; j < size.Events; j += size.Contexts)
                context.AppendLine(CultureInfo.InvariantCulture,
                    $"[System.Text.Json.Serialization.JsonSerializable(typeof(Bench.Common.Features.Feature{j % features}.Events.Changed{j}))]");
            context.AppendLine(CultureInfo.InvariantCulture,
                $"public sealed partial class ContractsJsonContext{i} : System.Text.Json.Serialization.JsonSerializerContext;");
            Add("Common", $"ContractsJsonContext{i}.cs", context.ToString());
        }
        for (var i = 0; i < features; i++)
        {
            var registration = new StringBuilder($"public static class Feature{i}PortiaExtensions {{ public static PortiaBuilder AddFeature{i}(this PortiaBuilder builder) {{\n");
            for (var j = i; j < size.Requests; j += features)
                registration.AppendLine(CultureInfo.InvariantCulture, $"builder.AddRequestHandler<Handlers.Change{j}Handler>();");
            for (var j = i; j < size.Processors; j += features)
            {
                registration.AppendLine(CultureInfo.InvariantCulture,
                    $"builder.AddProjector<Projectors.Projection{j}>(\"projection{j}\", WorkloadScope.Global);");
                registration.AppendLine(CultureInfo.InvariantCulture,
                    $"builder.AddReactor<Reactors.Reaction{j}>(\"reaction{j}\", WorkloadScope.Global);");
            }
            Add("Core", $"Features/Feature{i}/Feature{i}PortiaExtensions.cs", registration.AppendLine("return builder; } }").ToString());
        }
        var endpoints = new StringBuilder("public static class Endpoints { public static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app) {\n");
        for (var i = 0; i < size.Mappings; i++)
        {
            var request = i % size.Requests;
            endpoints.AppendLine(CultureInfo.InvariantCulture,
                $"app.MapPortiaPost<Bench.Common.Features.Feature{request % features}.Commands.Change{request}>(\"/change{i}\");");
        }
        Add("Api", "Endpoints.cs", endpoints.AppendLine("} }").ToString());
        var composition = new StringBuilder("public static class Composition { public static void Add(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { var builder = services.AddPortia();\n");
        for (var i = 0; i < features; i++)
            composition.AppendLine(CultureInfo.InvariantCulture, $"Bench.Core.Features.Feature{i}.Feature{i}PortiaExtensions.AddFeature{i}(builder);");
        Add("Worker", "Composition.cs", composition.AppendLine("} }").ToString());
        return new ApplicationFixture(Roles.ToDictionary(role => role,
            role => CSharpCompilation.Create("Bench." + role, files[role], References(role), CompilerHost.CompilationOptions),
            StringComparer.Ordinal));
    }

    static IEnumerable<MetadataReference> References(string role) => CompilerHost.References.Where(reference =>
    {
        var name = Path.GetFileNameWithoutExtension(reference.Display!);
        if (name.StartsWith("Portia.", StringComparison.Ordinal))
            return role == "Common" ? name == "Portia.Abstractions" :
                name is "Portia.Abstractions" or "Portia.Core" or "Portia.DependencyInjection"
                || role is "Api" or "Worker" && name == "Portia.AspNetCore";
        return name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
            || name is "System" or "mscorlib" or "netstandard" or "WindowsBase";
    });

    public ApplicationFixture Edit(string edit)
    {
        if (edit == "unchanged")
            return this;
        var (role, file, from, to) = edit switch
        {
            "body" => ("Core", "Change0Handler.cs", "=> 1;", "=> 2;"),
            "body-restore" => ("Core", "Change0Handler.cs", "=> 2;", "=> 1;"),
            "incomplete" => ("Core", "Change0Handler.cs", "=> 1;", "=> ;"),
            "metadata" => ("Common", "Change0.cs", "\"quantity\"", "\"units\""),
            "reference" => ("Common", "Change0.cs", ": IRequest, ICallable;", ": IRequest, ICallable { public int Revision { get; init; } }"),
            _ => throw new ArgumentException("Unknown application edit: " + edit, nameof(edit))
        };
        var result = new Dictionary<string, CSharpCompilation>(inputs, StringComparer.Ordinal);
        var compilation = result[role];
        var tree = compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("/" + file, StringComparison.Ordinal));
        var text = tree.GetText();
        var start = text.ToString().IndexOf(from, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("Application edit did not find its target.");
        result[role] = compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(text.WithChanges(new TextChange(new TextSpan(start, from.Length), to))));
        return new ApplicationFixture(result);
    }

    public ApplicationBuild Build(ApplicationBuild? previous = null, CancellationToken ct = default)
    {
        var results = new List<ApplicationProjectResult>();
        foreach (var role in Roles)
        {
            ct.ThrowIfCancellationRequested();
            var dependencies = role == "Common" ? [] : results.Where(project => project.Role == "Common"
                || role is "Api" or "Worker" && project.Role == "Core").ToArray();
            var references = References(role).Concat(dependencies.Select(project => project.Reference)).ToArray();
            var prior = previous?.Projects.Single(project => project.Role == role);
            var user = inputs[role];
            var input = prior is not null && ReferenceEquals(user, prior.User) && prior.Input.References.SequenceEqual(references)
                ? prior.Input : user.WithReferences(references);
            var driver = (prior?.Driver ?? CompilerHost.Driver()).RunGenerators(input, ct);
            var trees = driver.GetRunResult().GeneratedTrees;
            var output = prior is not null && ReferenceEquals(input, prior.Input)
                && prior.Driver.GetRunResult().GeneratedTrees.SequenceEqual(trees)
                ? prior.Output : input.AddSyntaxTrees(trees);
            results.Add(new ApplicationProjectResult(role, user, input, output, driver,
                prior is not null && ReferenceEquals(output, prior.Output) ? prior.Reference : output.ToMetadataReference()));
        }
        return new ApplicationBuild(results.ToArray());
    }

    public static async Task<Diagnostic[]> AnalyzeAsync(ApplicationBuild build, CancellationToken ct = default)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var project in build.Projects)
        {
            var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([], new FixtureOptions(project.Role)),
                null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
            diagnostics.AddRange(project.Output.GetDiagnostics(ct).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
            diagnostics.AddRange(await project.Output.WithAnalyzers([.. CompilerHost.CreateAnalyzers()], options)
                .GetAnalyzerDiagnosticsAsync(ct).ConfigureAwait(false));
        }
        return diagnostics.ToArray();
    }
}
