using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed partial class BenchmarkCase
{
    static CSharpCompilation AddAllocationWorkload(
        CSharpCompilation compilation,
        string operation,
        string size,
        CancellationToken ct
    )
    {
        var (reactors, files) = size switch
        {
            "minimal" => (50, 1),
            "small" => (200, 10),
            "standard" => (500, 50),
            "large" => (1000, 100),
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
        var trees = new List<SyntaxTree>();
        void Add(string name, string source) => trees.Add(CSharpSyntaxTree.ParseText(source, CompilerHost.ParseOptions,
            "/benchmark/Bench.Core/Features/Work/" + name + ".cs", cancellationToken: ct));

        if (operation.StartsWith("reactors-", StringComparison.Ordinal))
        {
            if (operation == "reactors-enabled")
                compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
                    compilation.Options.SpecificDiagnosticOptions.SetItem("PORTIA109", ReportDiagnostic.Warn)));

            Add("Messages", """
                            using Cntryl.Portia;
                            namespace Load;
                            public sealed record Changed : DomainEvent;
                            public sealed record CreateOrder : IRequest;
                            public sealed record CreateShipment : IRequest;
                            public sealed record CancelShipment : IRequest;
                            """);

            for (var index = 0; index < reactors + 2; index++)
            {
                var command = index < reactors ? "CreateOrder" : index == reactors ? "CreateShipment" : "CancelShipment";
                Add($"Reactors/R{index}", $$"""
                                         using Cntryl.Portia;
                                         using System.Threading;
                                         using System.Threading.Tasks;
                                         namespace Load;
                                         public sealed class R{{index}}(IRequestBus bus) : IReactorHandler<Changed>
                                         {
                                             public ValueTask<Result> HandleAsync(IReactorContext<Changed> context, CancellationToken ct) =>
                                                 bus.SendReactionAsync(new {{command}}(), context, ct);
                                         }
                                         """);
            }
        }
        else
        {
            var assignments = string.Concat(Enumerable.Repeat("value = Step(value);\n", 256));

            for (var index = 0; index < files; index++)
                Add($"Models/C{index}", $$"""
                                       namespace Load;
                                       public static class C{{index}}
                                       {
                                           static int Step(int n) => n + 1;
                                           public static int Work(int value) { {{assignments}} return value; }
                                       }
                                       """);

            if (operation == "repeated-calls")
                Add("Handlers/QueryHandler", """
                                             using Cntryl.Portia;
                                             using System.Threading;
                                             using System.Threading.Tasks;
                                             namespace Bench.Common.Features.Work.Queries { public sealed record Lookup : IRequest; }
                                             namespace Load
                                             {
                                                 using Bench.Common.Features.Work.Queries;
                                                 public sealed class QueryHandler(Reader reader) : IRequestHandler<Lookup>
                                                 {
                                                     public ValueTask<Result> HandleAsync(IRequestContext<Lookup> context, CancellationToken ct) =>
                                                         reader.Read(ct);
                                                 }
                                                 public sealed class Reader(IAggregateReader reader, Aggregate aggregate)
                                                 {
                                                     public async ValueTask<Result> Read(CancellationToken ct)
                                                     {
                                                         await reader.HydrateAsync(aggregate, ct);
                                                         return Result.Success;
                                                     }
                                                 }
                                             }
                                             """);
        }

        return compilation.AddSyntaxTrees(trees);
    }
}
