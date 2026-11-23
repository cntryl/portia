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
                new XElement("EnforceCodeStyleInBuild", "false"),
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
        project.Add(new XElement("ItemGroup",
            new XElement("Compile", new XAttribute("Remove", "Contracts/**/*.cs")),
            new XElement("ProjectReference", new XAttribute("Include", "Contracts/Bench.Replay.Common.csproj"))));
        Directory.CreateDirectory(Path.Combine(directory, "Contracts"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Contracts/Bench.Replay.Common.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><GenerateDocumentationFile>false</GenerateDocumentationFile><EnableNETAnalyzers>false</EnableNETAnalyzers><EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild><TreatWarningsAsErrors>false</TreatWarningsAsErrors><IsPackable>false</IsPackable></PropertyGroup></Project>", ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "Contracts/SharedContract.cs"), """
            namespace Bench.Replay.Common;
            public sealed record SharedContract(int Value_PORTIA_CROSS_0);
            """, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "CrossProject.cs"), """
            #warning PORTIA_CROSS_REPAIR_0
            namespace Bench.Editor;
            public static class CrossProject
            {
                public static int Read(Bench.Replay.Common.SharedContract value) => value.Value_PORTIA_CROSS_0;
            }
            """, ct).ConfigureAwait(false);
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
        var files = new[] { "Projector.cs", "Request.cs", "Editing.cs", "CrossProject.cs", "Contracts/SharedContract.cs" };
        var scaffoldFiles = new[] { "Bench.Editor.csproj", "Contracts/Bench.Replay.Common.csproj" };
        await File.WriteAllTextAsync(Path.Combine(directory, "portia-benchmark-workspace.json"),
            JsonSerializer.Serialize(new
            {
                format = "portia-editor-benchmark-2",
                files,
                scaffoldFiles,
                scaffoldHashes = scaffoldFiles.ToDictionary(file => file, file => CompilerHost.FileHash(Path.Combine(directory, file))),
                initialHashes = files.ToDictionary(file => file,
                    file => CompilerHost.FileHash(Path.Combine(directory, file))),
                subjectAssemblies = project.Descendants("Analyzer")
                    .Select(element => element.Attribute("Include")!.Value)
                    .Concat(project.Descendants("HintPath").Select(element => element.Value)).Distinct(StringComparer.Ordinal)
                    .ToDictionary(path => path, CompilerHost.FileHash)
            }, Campaign.Json), ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(directory, ".vscode"));
        await File.WriteAllTextAsync(Path.Combine(directory, ".vscode/settings.json"), """
            { "dotnet.backgroundAnalysis.analyzerDiagnosticsScope": "openFiles", "dotnet.backgroundAnalysis.compilerDiagnosticsScope": "openFiles", "editor.formatOnSave": false }
            """, ct).ConfigureAwait(false);
        Console.WriteLine(directory);
    }
}
