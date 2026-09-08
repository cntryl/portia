using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

static class GeneratorCompilation
{
    public static IReadOnlyList<Diagnostic> Diagnostics(string source, params IIncrementalGenerator[] generators)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Diagnostics", [CSharpSyntaxTree.ParseText(source, parseOptions, path: "DiagnosticScenario.cs")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators.Select(generator => generator.AsSourceGenerator()), parseOptions: parseOptions);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }

    public static Assembly Compile(string source, params IIncrementalGenerator[] generators)
        => Compile(source, null, generators);

    public static Assembly CompileSuppressing(string source, string diagnosticId, params IIncrementalGenerator[] generators)
        => Compile(source, diagnosticId, generators);

    static Assembly Compile(string source, string? suppressedDiagnostic, params IIncrementalGenerator[] generators)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        if (suppressedDiagnostic is not null)
            options = options.WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions.SetItem(suppressedDiagnostic, ReportDiagnostic.Suppress));
        var compilation = CSharpCompilation.Create(
            "Consumer_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parseOptions, path: "ConsumerScenario.cs")],
            references,
            options);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators.Select(generator => generator.AsSourceGenerator()), parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.All(driver.GetRunResult().Results, result => Assert.Null(result.Exception));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    public static string GeneratedSource(string source, params IIncrementalGenerator[] generators)
        => GeneratedSource(source, [], generators);

    public static string GeneratedSource(
        string source,
        IReadOnlyCollection<MetadataReference> additionalReferences,
        params IIncrementalGenerator[] generators)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat(additionalReferences);
        var compilation = CSharpCompilation.Create("GeneratedSource", [CSharpSyntaxTree.ParseText(source, parseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators.Select(generator => generator.AsSourceGenerator()), parseOptions: parseOptions);
        driver = driver.RunGenerators(compilation);
        Assert.All(driver.GetRunResult().Results, result => Assert.Null(result.Exception));
        return string.Join(Environment.NewLine, driver.GetRunResult().GeneratedTrees.Select(tree => tree.GetText().ToString()));
    }

    public static MetadataReference Reference(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Reference_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }
}
