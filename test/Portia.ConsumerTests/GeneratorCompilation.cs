using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

static class GeneratorCompilation
{
    public static IReadOnlyList<Diagnostic> Diagnostics(string source, params IIncrementalGenerator[] generators)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Diagnostics", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators.Select(generator => generator.AsSourceGenerator()));
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }

    public static Assembly Compile(string source, params IIncrementalGenerator[] generators)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parseOptions, path: "ConsumerScenario.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
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
}
