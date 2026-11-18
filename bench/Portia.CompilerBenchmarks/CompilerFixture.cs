using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed record FixtureSize(int Requests, int Events, int Processors, int Mappings, int Contexts, int Unrelated)
{
    public static FixtureSize Named(string name) => name switch
    {
        "minimal" => new(2, 2, 2, 2, 1, 2),
        "small" => new(20, 20, 10, 10, 2, 100),
        "standard" => new(200, 200, 50, 40, 5, 2000),
        "large" => new(1000, 1000, 200, 100, 10, 10000),
        _ => throw new ArgumentException("Fixture must be minimal, small, standard, or large.", nameof(name))
    };
}

sealed record CompilerFixture(CSharpCompilation Compilation, FixtureSize Size)
{
    const string Header =
        "using System; using System.Threading; using System.Threading.Tasks; using Cntryl.Portia; namespace Bench.Core.Features.Work;\n";

    public static readonly string[] Edits =
        ["unchanged", "body", "location", "route", "metadata", "event-add", "incomplete"];

    public static CompilerFixture Create(string sizeName, string identity = "Bench.Core", bool negative = false)
    {
        var size = FixtureSize.Named(sizeName);
        var files = new List<SyntaxTree>();

        void Add(string path, string text) => files.Add(CSharpSyntaxTree.ParseText(text, CompilerHost.ParseOptions,
            "/benchmark/Bench.Core/" + path, Encoding.UTF8));

        Add("Features/Work/Execution/Editing.cs",
            Header + "public static class Editing { public static int Value() => 1; }");
        for (var i = 0; i < size.Unrelated; i++)
            Add($"Features/Work/Models/Unrelated{i}.cs",
                Header + $"public sealed class Unrelated{i} {{ public int Value {{ get; init; }} }}");
        if (!negative)
        {
            for (var i = 0; i < size.Requests; i++)
            {
                Add($"Features/Work/Commands/Command{i}.cs", Header + $$"""
                                                                        [Discriminator("bench.command{{i}}", 1)][RequestRoute("app", "bench", "command", "run{{i}}")]
                                                                        public sealed record Command{{i}}([property: System.ComponentModel.Description("quantity")] [property: System.ComponentModel.DataAnnotations.Range(0, 100)] int Id = 1) : IRequest, ICallable;
                                                                        """);
                Add($"Features/Work/Handlers/Handler{i}.cs", Header + $$"""
                                                                        public sealed class Handler{{i}} : IRequestHandler<Command{{i}}>
                                                                        { public ValueTask<Result> HandleAsync(IRequestContext<Command{{i}}> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }
                                                                        """);
            }

            for (var i = 0; i < size.Events; i++)
                Add($"Features/Work/Events/Event{i}.cs",
                    Header +
                    $"[Discriminator(\"bench.event{i}\", 1)] public sealed record Event{i}(int Value) : DomainEvent;");
            for (var i = 0; i < size.Processors; i++)
                Add($"Features/Work/Projectors/Projection{i}.cs", Header + $$"""
                                                                             public sealed partial class Projection{{i}} : Projector, IProjectorHandler<Event0>
                                                                             { public Projection{{i}}() : base(null!, EventStreamPattern.ForPattern("events")) { }
                                                                             public ValueTask<Result> HandleAsync(IProjectorContext<Event0> context, CancellationToken ct) => ValueTask.FromResult(Result.Success); }
                                                                             """);
            for (var i = 0; i < size.Contexts; i++)
            {
                var source = new StringBuilder(Header).AppendLine("[PortiaJsonContext]");
                for (var j = i; j < size.Requests; j += size.Contexts)
                    source.AppendLine(CultureInfo.InvariantCulture,
                        $"[System.Text.Json.Serialization.JsonSerializable(typeof(Command{j}))]");
                for (var j = i; j < size.Events; j += size.Contexts)
                    source.AppendLine(CultureInfo.InvariantCulture,
                        $"[System.Text.Json.Serialization.JsonSerializable(typeof(Event{j}))]");
                source.AppendLine(CultureInfo.InvariantCulture,
                    $"internal sealed partial class JsonContext{i} : System.Text.Json.Serialization.JsonSerializerContext;");
                Add($"Features/Work/Serialization/JsonContext{i}.cs", source.ToString());
            }

            var composition =
                new StringBuilder(
                        "using Cntryl.Portia; using Microsoft.Extensions.DependencyInjection; using Microsoft.AspNetCore.Routing; namespace Bench.Core.Features.Work;\n")
                    .AppendLine(
                        "public static class Composition { public static void Add(IServiceCollection services) { var builder = services.AddPortia();");
            for (var i = 0; i < size.Requests; i++)
                composition.AppendLine(CultureInfo.InvariantCulture, $"builder.AddRequestHandler<Handler{i}>();");
            Add("Features/Work/Composition.cs", composition.AppendLine("} }").ToString());
            Add("Features/Work/Catalog.cs",
                Header +
                "public static class Catalog { public static void Add(DomainEventTypeCatalog catalog) => catalog.AddPortiaGeneratedDomainEvents(); }");
            var endpoints =
                new StringBuilder(
                        "using Cntryl.Portia; using Microsoft.AspNetCore.Routing; namespace Bench.Core.Features.Work;\n")
                    .AppendLine("public static class Endpoints { public static void Map(IEndpointRouteBuilder app) {");
            for (var i = 0; i < size.Mappings; i++)
                endpoints.AppendLine(CultureInfo.InvariantCulture,
                    $"app.MapPortiaPost<Command{i % size.Requests}>(\"/command{i}\");");
            Add("Features/Work/Endpoints.cs", endpoints.AppendLine("} }").ToString());
        }

        return new CompilerFixture(
            CSharpCompilation.Create(identity, files, CompilerHost.References, CompilerHost.CompilationOptions), size);
    }

    public CompilerFixture Edit(string edit)
    {
        if (edit == "unchanged")
            return this;
        if (edit == "event-add")
        {
            var added = CSharpSyntaxTree.ParseText(
                Header + "[Discriminator(\"bench.extra\", 1)] public sealed record ExtraEvent : DomainEvent;",
                CompilerHost.ParseOptions, "/benchmark/Bench.Core/Features/Work/Events/ExtraEvent.cs", Encoding.UTF8);
            return this with
            {
                Compilation = Replace("JsonContext0.cs", source => source.Replace("[PortiaJsonContext]",
                    "[PortiaJsonContext][System.Text.Json.Serialization.JsonSerializable(typeof(ExtraEvent))]",
                    StringComparison.Ordinal)).AddSyntaxTrees(added)
            };
        }

        var (file, oldText, newText) = edit switch
        {
            "body" => ("Editing.cs", "=> 1;", "=> 2;"),
            "location" => ("Endpoints.cs", "public static class", "\n\npublic static class"),
            "route" => ("Endpoints.cs", "/command0", "/changed0"),
            "metadata" => ("Command0.cs", "\"quantity\"", "\"units\""),
            "incomplete" => ("Editing.cs", "=> 1;", "=> ;"),
            _ => throw new ArgumentException("Unknown edit: " + edit, nameof(edit))
        };
        return this with
        {
            Compilation = Replace(file, source => source.Replace(oldText, newText, StringComparison.Ordinal))
        };
    }

    CSharpCompilation Replace(string file, Func<string, string> change)
    {
        var tree = Compilation.SyntaxTrees.Single(item => item.FilePath.EndsWith("/" + file, StringComparison.Ordinal));
        var before = tree.GetText();
        var after = change(before.ToString());
        if (after == before.ToString())
            throw new InvalidOperationException("Edit did not change " + file);
        var oldText = before.ToString();
        var prefix = 0;
        while (prefix < oldText.Length && prefix < after.Length && oldText[prefix] == after[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < after.Length - prefix
                                                && oldText[oldText.Length - suffix - 1] ==
                                                after[after.Length - suffix - 1])
            suffix++;
        var changeSpan = new TextSpan(prefix, oldText.Length - prefix - suffix);
        var changeText = after.Substring(prefix, after.Length - prefix - suffix);
        return Compilation.ReplaceSyntaxTree(tree,
            tree.WithChangedText(before.WithChanges(new TextChange(changeSpan, changeText))));
    }
}
