using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Cntryl.Portia.RepositoryTools;

static class PackedCompilerVerifier
{
    internal static async Task<int> VerifyAsync(string[] args)
    {
        var version = ReadOption(args, "--version");
        var packages = Path.GetFullPath(ReadOption(args, "--packages"));
        if (!Directory.Exists(packages))
            throw new DirectoryNotFoundException($"Package directory does not exist: {packages}");

        var repository = FindRepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), $"portia-packed-compiler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            return await VerifyInDirectoryAsync(repository, packages, version, temporary).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    static async Task<int> VerifyInDirectoryAsync(string repository, string packages, string version, string temporary)
    {
        var cache = Path.Combine(temporary, "cache");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = cache
        };
        var config = XDocument.Load(Path.Combine(repository,
            "smoke/Portia.AspNetCore.NativeAotConsumer/NuGet.Config"));
        var localSource = config.Descendants("add")
            .SingleOrDefault(element => (string?)element.Attribute("key") == "portia-local")
                          ?? throw new InvalidDataException("The NuGet.Config does not define the portia-local package source.");
        localSource.SetAttributeValue("value", packages);
        config.Save(Path.Combine(temporary, "NuGet.Config"));

        foreach (var generalAnalyzer in new[] { true, false })
        {
            var projectPath = Path.Combine(temporary, "Probe.csproj");
            WriteProbeProject(projectPath, version, generalAnalyzer);
            await RunRequiredAsync(temporary, environment, "restore", "Probe.csproj", "--verbosity", "quiet")
                .ConfigureAwait(false);

            foreach (var fixture in CreateFixtures(generalAnalyzer))
            {
                await File.WriteAllTextAsync(Path.Combine(temporary, "Probe.cs"), fixture.Source)
                    .ConfigureAwait(false);
                var result = await ProcessRunner.RunAsync("dotnet",
                    ["build", "Probe.csproj", "--configuration", "Release", "--no-restore", "--verbosity", "quiet"],
                    temporary, environment).ConfigureAwait(false);
                if (fixture.ExpectedDiagnostic is null)
                {
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException(result.Output);
                }
                else if (result.ExitCode == 0 || !HasSourceDiagnostic(result.Output, fixture.ExpectedDiagnostic))
                {
                    throw new InvalidOperationException($"Expected source diagnostic {fixture.ExpectedDiagnostic}:{Environment.NewLine}{result.Output}");
                }
                else
                {
                    Console.WriteLine($"Packed compiler consumer: {fixture.ExpectedDiagnostic} verified");
                }
            }
        }

        var fallback = Path.Combine(temporary, "Fallback");
        Directory.CreateDirectory(fallback);
        var fallbackProject = CreateProbeProject(version, generalAnalyzer: false, executable: true,
            excludeHttpAnalyzer: true);
        fallbackProject.Save(Path.Combine(fallback, "Probe.csproj"));
        await File.WriteAllTextAsync(Path.Combine(fallback, "Probe.cs"), FallbackSource).ConfigureAwait(false);
        await RunRequiredAsync(temporary, environment, "restore", "Fallback/Probe.csproj", "--verbosity", "quiet")
            .ConfigureAwait(false);
        await RunRequiredAsync(temporary, environment, "run", "--project", "Fallback/Probe.csproj",
            "--configuration", "Release", "--no-restore", "--verbosity", "quiet").ConfigureAwait(false);
        Console.WriteLine("Packed runtime fallback verified with HTTP analyzer assets excluded.");
        Console.WriteLine("Packed compiler qualification passed with and without the independent general analyzer package.");
        return 0;
    }

    static string ReadOption(string[] args, string option)
    {
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == option)
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"Expected a value after {option}.");
                return args[index];
            }
        }

        throw new ArgumentException($"Missing required option {option}.");
    }

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Portia.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Portia.slnx above the repository-tools assembly.");
    }

    static void WriteProbeProject(string path, string version, bool generalAnalyzer) =>
        CreateProbeProject(version, generalAnalyzer).Save(path);

    static XDocument CreateProbeProject(string version, bool generalAnalyzer, bool executable = false,
        bool excludeHttpAnalyzer = false)
    {
        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"),
            new XElement("OutputType", executable ? "Exe" : "Library"),
            new XElement("Nullable", "enable"),
            new XElement("TreatWarningsAsErrors", "true"),
            new XElement("JsonSerializerIsReflectionEnabledByDefault", "false"));
        var references = new List<XElement>
        {
            PackageReference("Cntryl.Portia.AspNetCore", version,
                excludeHttpAnalyzer ? "analyzers;build;buildTransitive" : null),
            PackageReference("Cntryl.Portia.Testing", version)
        };
        if (generalAnalyzer)
            references.Add(new XElement("PackageReference",
                new XAttribute("Include", "Cntryl.Portia.Analyzers"),
                new XAttribute("Version", version),
                new XAttribute("IncludeAssets", "analyzers;build;buildTransitive")));

        var root = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk.Web"),
            propertyGroup,
            new XElement("ItemGroup", references));
        if (excludeHttpAnalyzer)
        {
            root.Add(new XElement("Target",
                new XAttribute("Name", "RemoveHttpTooling"),
                new XAttribute("BeforeTargets", "CoreCompile"),
                new XElement("ItemGroup",
                    new XElement("Analyzer",
                        new XAttribute("Remove", "@(Analyzer)"),
                        new XAttribute("Condition", "'%(Analyzer.Filename)' == 'Portia.AspNetCore.Generators'")))));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    static XElement PackageReference(string name, string version, string? excludeAssets = null)
    {
        var element = new XElement("PackageReference",
            new XAttribute("Include", name),
            new XAttribute("Version", version));
        if (excludeAssets is not null)
            element.SetAttributeValue("ExcludeAssets", excludeAssets);
        return element;
    }

    static List<ProbeFixture> CreateFixtures(bool generalAnalyzer)
    {
        var fixtures = new List<ProbeFixture>
        {
            new(Prefix + "public static class App { public static void Map(IEndpointRouteBuilder routes) => routes.MapPortiaGet<Command>(\"/probe\"); }", null),
            new(Prefix + "public static class App { public static void Map() { Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGet<Command>; } }", "PORTIA016")
        };
        if (generalAnalyzer)
        {
            foreach (var request in new[]
                     {
                         (Type: "Command", Declaration: ""),
                         (Type: "Query", Declaration: "public sealed record Query : IRequest<string>;"),
                         (Type: "Feed", Declaration: "public sealed record Feed : IStreamRequest<string>;")
                     })
            {
                foreach (var discard in new[]
                         {
                             "_ = value.ExpectDenied();",
                             "Action forgotten = () => value.ExpectDenied();"
                         })
                {
                    var source = Prefix + request.Declaration +
                                 $"public static class App {{ public static async System.Threading.Tasks.Task Check(IServiceProvider services) {{ var value = RequestScenario.For(services).When(new {request.Type}()); {discard} await value; }} }}";
                    fixtures.Add(new ProbeFixture(source, "PORTIA107"));
                }
            }

            fixtures.Add(new ProbeFixture(Prefix + "[PortiaJsonContext] public class InvalidContext { }", "PORTIA030"));
        }

        return fixtures;
    }

    static bool HasSourceDiagnostic(string output, string expected) =>
        output.Split('\n').Any(line => line.Contains("Probe.cs(", StringComparison.Ordinal)
                                        && line.Contains($"error {expected}:", StringComparison.Ordinal));

    static async Task RunRequiredAsync(string workingDirectory, IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("dotnet", arguments, workingDirectory, environment)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Output);
    }

    const string Prefix = """
        using System;
        using Cntryl.Portia;
        using Cntryl.Portia.Testing;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Routing;
        using System.Text.Json.Serialization;
        [Discriminator("probe.command")] public sealed record Command : IRequest, ICallable;
        [PortiaJsonContext, JsonSerializable(typeof(Command))]
        internal partial class ProbeJsonContext : JsonSerializerContext;
        """;

    const string FallbackSource = """
        using System;
        using Cntryl.Portia;
        using Microsoft.AspNetCore.Builder;
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var rejected = false;
        try { app.MapPortiaGet<Command>("/probe"); }
        catch (InvalidOperationException error) when (error.Message.Contains("Cntryl.Portia.AspNetCore", StringComparison.Ordinal)
            && error.Message.Contains("analyzer assets", StringComparison.Ordinal)
            && !error.Message.Contains("Portia.DependencyInjection", StringComparison.Ordinal)) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Packed fallback did not identify the HTTP generator owner.");
        public sealed record Command : IRequest, ICallable;
        """;

    sealed record ProbeFixture(string Source, string? ExpectedDiagnostic);
}
