using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.CompilerBenchmarks;

static class GeneratorChecks
{
    public static void Verify()
    {
        var fixture = CompilerFixture.Create("minimal");
        var compilation = fixture.Compilation;
        var context =
            compilation.SyntaxTrees.Single(tree =>
                tree.FilePath.EndsWith("/JsonContext0.cs", StringComparison.Ordinal));
        var request =
            compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("/Command0.cs", StringComparison.Ordinal));
        var invalid = CSharpSyntaxTree.ParseText(request.GetText().ToString().Replace("Range(0, 100)",
                "Range(typeof(int), \"0\", \"100\")", StringComparison.Ordinal), CompilerHost.ParseOptions,
            request.FilePath, Encoding.UTF8);
        compilation = compilation.RemoveSyntaxTrees(context).ReplaceSyntaxTree(request, invalid).AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("""
                                       using System.Threading; using System.Threading.Tasks; using Cntryl.Portia;
                                       namespace Bench.Core.Features.Work;
                                       public sealed class Unsupported<T> : IRequestHandler<Command0>
                                       { public ValueTask<Result> HandleAsync(IRequestContext<Command0> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }
                                       public sealed record InvalidMetadata : IRequest
                                       { [System.ComponentModel.DataAnnotations.Range(typeof(int), "0", "100")] public int Value { get; init; } }
                                       """, CompilerHost.ParseOptions, "/benchmark/Bench.Core/Unsupported.cs",
                Encoding.UTF8));
        var result = CompilerHost.Driver(tracking: true).RunGenerators(compilation).GetRunResult();

        if (result.Results.Any(item => item.Exception is not null))
            throw new InvalidOperationException("Generator diagnostic control threw an exception.");

        Verification.Coverage(["PORTIA015", "PORTIA025", "PORTIA031"],
            result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Select(item => item.Id)
                .Distinct());
        var baseline = CompilerHost.Driver(tracking: true).RunGenerators(fixture.Compilation);
        var edited = baseline.RunGenerators(fixture.Edit("body").Compilation);
        var stages = edited.GetRunResult().Results.SelectMany(item => item.TrackedSteps).GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.SelectMany(item => item.Value).ToArray(),
                StringComparer.Ordinal);

        foreach (var name in new[]
                 {
                     "PortiaHttpBindingShapes", "PortiaProcessorImplementations", "DomainEventCatalogRegistrations",
                     "PortiaSharedRegistrations"
                 })
        {
            if (!stages.TryGetValue(name, out var steps) || !steps.SelectMany(step => step.Outputs).Any()
                                                         || steps.SelectMany(step => step.Outputs).Any(output =>
                                                             output.Reason is not (IncrementalStepRunReason.Cached
                                                                 or IncrementalStepRunReason.Unchanged)))
                throw new InvalidOperationException("Unrelated edit invalidated stable model stage: " + name);
        }
    }
}
