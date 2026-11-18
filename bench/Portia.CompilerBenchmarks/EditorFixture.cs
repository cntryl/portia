using System.Text.Json;
using System.Xml.Linq;

namespace Cntryl.Portia.CompilerBenchmarks;

static class EditorFixture
{
    public static async Task ExportAsync(string output, CancellationToken ct)
    {
        var directory = Path.GetFullPath(output);
        if (Directory.Exists(directory))
            throw new InvalidOperationException("Use a new directory for each disposable editor fixture.");
        Directory.CreateDirectory(directory);
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net11.0"),
                new XElement("OutputType", "Library"),
                new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                new XElement("GenerateDocumentationFile", "false"), new XElement("TreatWarningsAsErrors", "false"),
                new XElement("EnableNETAnalyzers", "false"), new XElement("IsPackable", "false"),
                new XElement("PortiaProjectStructure", "false"),
                new XElement("InterceptorsNamespaces", "Cntryl.Portia.Generated")),
            new XElement("ItemGroup",
                new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.AspNetCore.App")),
                CompilerHost.References.Where(reference =>
                        Path.GetFileName(reference.Display!).StartsWith("Portia.", StringComparison.Ordinal))
                    .Select(reference => new XElement("Reference",
                        new XAttribute("Include", Path.GetFileNameWithoutExtension(reference.Display!)),
                        new XElement("HintPath", reference.Display))),
                new[]
                    {
                        typeof(DomainEventCatalogGenerator).Assembly, typeof(RequestHttpBindingGenerator).Assembly,
                        typeof(JsonMetadataCodeFixProvider).Assembly
                    }
                    .Select(assembly => new XElement("Analyzer", new XAttribute("Include", assembly.Location)))));
        await File.WriteAllTextAsync(Path.Combine(directory, "Bench.Editor.csproj"), project.ToString(), ct)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "Projector.cs"), """
                                                                              using Cntryl.Portia;
                                                                              namespace Bench.Editor;
                                                                              [Discriminator("bench.changed", 1)] public sealed record Changed : DomainEvent;
                                                                              public sealed class Projection_PORTIA_BENCH_0 : Projector, IProjectorHandler<Changed>
                                                                              {
                                                                                  public Projection_PORTIA_BENCH_0() : base(null!, EventStreamPattern.ForPattern("events")) { }
                                                                                  public ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                                                                              }
                                                                              [PortiaJsonContext]
                                                                              [System.Text.Json.Serialization.JsonSerializable(typeof(Changed))]
                                                                              [System.Text.Json.Serialization.JsonSerializable(typeof(Query))]
                                                                              internal sealed partial class Context : System.Text.Json.Serialization.JsonSerializerContext;
                                                                              """, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "Request.cs"), """
                                                                            using Cntryl.Portia;
                                                                            namespace Bench.Editor;
                                                                            public sealed record Query : IRequest
                                                                            {
                                                                                [System.ComponentModel.DataAnnotations.RegularExpression(".*")]
                                                                                public string Name { get; init; } = "";
                                                                            }
                                                                            """, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "Editing.cs"), """
                                                                            #warning PORTIA_BENCH_0
                                                                            namespace Bench.Editor;
                                                                            public static class Editing { public static int Value() => 1; }
                                                                            """, ct).ConfigureAwait(false);
        var files = new[] { "Projector.cs", "Request.cs", "Editing.cs" };
        await File.WriteAllTextAsync(Path.Combine(directory, "portia-benchmark-workspace.json"),
            JsonSerializer.Serialize(new
            {
                format = "portia-editor-benchmark-1",
                files,
                initialHashes = files.ToDictionary(file => file,
                    file => CompilerHost.FileHash(Path.Combine(directory, file))),
                subjectAssemblies = project.Descendants("Analyzer")
                    .Select(element => element.Attribute("Include")!.Value)
                    .ToDictionary(path => path, CompilerHost.FileHash)
            }, Campaign.Json), ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(directory, ".vscode"));
        await File.WriteAllTextAsync(Path.Combine(directory, ".vscode/settings.json"), """
            { "dotnet.backgroundAnalysis.analyzerDiagnosticsScope": "openFiles", "dotnet.backgroundAnalysis.compilerDiagnosticsScope": "openFiles", "editor.formatOnSave": false }
            """, ct).ConfigureAwait(false);
        Console.WriteLine(directory);
    }
}
