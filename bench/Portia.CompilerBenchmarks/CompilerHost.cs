using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.CompilerBenchmarks;

static class CompilerHost
{
    public const string ReferencePackVersion = "11.0.0";

    public static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest)
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);

    public static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable);

    public static readonly Type[] Generators =
    [
        typeof(DomainEventCatalogGenerator), typeof(JsonMetadataDiagnosticGenerator), typeof(JsonRootMarkerGenerator),
        typeof(PortiaServiceRegistrationGenerator), typeof(ProjectorReactorEventDispatcherGenerator),
        typeof(RegistrationCallInterceptorGenerator), typeof(RequestHttpBindingGenerator),
        typeof(RequestShapeDiagnosticsGenerator)
    ];

    public static readonly Type[] Analyzers =
    [
        typeof(ApplicationStructureAnalyzer), typeof(ApplicationBoundaryAnalyzer), typeof(ApplicationContractAnalyzer),
        typeof(ComponentPracticeAnalyzer), typeof(ScenarioObservationAnalyzer), typeof(AggregateOnHandlerAnalyzer),
        typeof(RequestActorAnalyzer)
    ];

    public static readonly Type[] CodeFixes =
    [
        typeof(ApplicationStructureCodeFixProvider), typeof(JsonMetadataCodeFixProvider),
        typeof(PartialProcessorCodeFixProvider)
    ];

    public static readonly ImmutableArray<MetadataReference> References =
    [
        .. (Environment.GetEnvironmentVariable("PORTIA_COMPILER_REFERENCES") is { } manifest
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ??
              throw new InvalidOperationException("Empty reference manifest.")
            : ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        .Where(path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
                       && Path.GetFileNameWithoutExtension(path) is not ("Portia.CompilerBenchmarks"
                           or "Portia.Generators"
                           or "Portia.AspNetCore.Generators" or "Portia.CodeFixes"))
        .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path))
    ];

    // An explicit path makes SDK/reference-pack selection reproducible rather than choosing the newest installed generator.
    public static string JsonGeneratorPath => Path.GetFullPath(
        Environment.GetEnvironmentVariable("PORTIA_JSON_GENERATOR")
        ?? Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "../../..", "packs",
            "Microsoft.NETCore.App.Ref", ReferencePackVersion,
            "analyzers/dotnet/cs/System.Text.Json.SourceGeneration.dll"));

    public static GeneratorDriver Driver(bool tracking = false, string component = "all", bool withJson = true)
    {
        var types = component == "all" ? Generators : [Generators.Single(type => type.Name == component)];
        var generators = types
            .Select(type => ((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator()).ToList();

        if (withJson)
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(JsonGeneratorPath));
            generators.Add(((IIncrementalGenerator)Activator.CreateInstance(assembly.GetType(
                "System.Text.Json.SourceGeneration.JsonSourceGenerator", throwOnError: true)!)!).AsSourceGenerator());
        }


        return CSharpGeneratorDriver.Create(generators, parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(default, tracking));
    }

    public static DiagnosticAnalyzer[] CreateAnalyzers(string component = "all") => (component == "all"
            ? Analyzers
            : [Analyzers.Single(type => type.Name == component)])
        .Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!).ToArray();

    public static void VerifyInventory()
    {
        var assemblies = new[]
        {
            typeof(DomainEventCatalogGenerator).Assembly, typeof(RequestHttpBindingGenerator).Assembly,
            typeof(JsonMetadataCodeFixProvider).Assembly
        };
        var actual = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes()).Where(type => !type.IsAbstract &&
                (typeof(IIncrementalGenerator).IsAssignableFrom(type) || typeof(DiagnosticAnalyzer).IsAssignableFrom(
                                                                          type)
                                                                      || typeof(CodeFixProvider)
                                                                          .IsAssignableFrom(type)))
            .Select(type => type.FullName!);
        Verification.Coverage(Generators.Concat(Analyzers).Concat(CodeFixes).Select(type => type.FullName!), actual);
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static IReadOnlyDictionary<string, string> Sources(GeneratorDriver driver) => driver.GetRunResult().Results
        .SelectMany(result => result.GeneratedSources.Select(source =>
            (Key: result.Generator.GetGeneratorType().Name + "/" + source.HintName,
                Value: source.SourceText.ToString())))
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    public static void CheckRun(GeneratorDriver driver, Compilation input, bool compile = true)
    {
        var result = driver.GetRunResult();
        var failures = result.Results.Where(item => item.Exception is not null)
            .Select(item => item.Exception!.ToString())
            .Concat(result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                .Select(item => item.ToString())).ToList();

        if (compile)
        {
            var output = input.AddSyntaxTrees(result.GeneratedTrees);
            using var stream = new MemoryStream();
            failures.AddRange(output.Emit(stream).Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                .Select(item => item.ToString()));
        }


        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Take(20)));
    }
}

sealed class FixtureOptions(string role, bool enabled = true, bool requireRequestActor = false) : AnalyzerConfigOptionsProvider
{
    readonly Values _values = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["build_property.PortiaRequireRequestActor"] = requireRequestActor ? "true" : "false",
        ["build_property.PortiaProjectStructure"] = enabled ? "true" : "false",
        ["build_property.MSBuildProjectDirectory"] = "/benchmark/Bench." + role,
        ["build_property.MSBuildProjectName"] = "Bench." + role,
        ["build_property.RootNamespace"] = "Bench." + role,
        ["build_property.IsTestProject"] = role == "Tests" ? "true" : "false"
    });

    static readonly Values Empty = new(new Dictionary<string, string>());
    public override AnalyzerConfigOptions GlobalOptions => _values;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;

    sealed class Values(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
