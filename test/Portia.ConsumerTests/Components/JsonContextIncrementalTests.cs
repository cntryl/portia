using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

public sealed class JsonContextIncrementalTests
{
    const string Composition = """
        using Cntryl.Portia;
        using Microsoft.Extensions.DependencyInjection;
        public static class Composition
        {
            public static PortiaBuilder Configure(IServiceCollection services) => services.AddPortia();
        }
        """;

    [Fact]
    public void ShouldReuseSharedRegistrationsGivenUnchangedReferencedContexts()
    {
        // Arrange
        var options = Options();
        var unrelated = CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value() => 1; }",
            options, "Unrelated.cs");
        var compilation = Compilation(options, unrelated, ContextReference("FirstFactory"));
        var driver = Driver(options).RunGenerators(compilation);
        var original = Generated(driver);

        driver = driver.RunGenerators(compilation);
        AssertReasons(driver, "PortiaSharedRegistrations", IncrementalStepRunReason.Cached);

        var edited = CSharpSyntaxTree.ParseText("public static class Unrelated { public static int Value() => 2; }",
            options, "Unrelated.cs");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(unrelated, edited));

        AssertReasons(driver, "PortiaReferencedJsonContexts", IncrementalStepRunReason.Unchanged);

        // Act
        AssertReasons(driver, "PortiaSharedRegistrations", IncrementalStepRunReason.Cached);

        // Assert
        Assert.Equal(original, Generated(driver));
        Assert.Contains("global::Contracts.FirstFactory.Create(options)", original, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRefreshSharedRegistrationsGivenReplacedContextFactory()
    {
        // Arrange
        var options = Options();
        var reference = ContextReference("FirstFactory");
        var compilation = Compilation(options,
            CSharpSyntaxTree.ParseText("public sealed class Unrelated;", options, "Unrelated.cs"), reference);

        // Act
        var driver = Driver(options).RunGenerators(compilation);

        // Assert
        Assert.Contains("global::Contracts.FirstFactory.Create(options)", Generated(driver), StringComparison.Ordinal);

        driver = driver.RunGenerators(compilation.ReplaceReference(reference, ContextReference("SecondFactory")));

        AssertReasons(driver, "PortiaReferencedJsonContexts", IncrementalStepRunReason.Modified);
        AssertReasons(driver, "PortiaSharedRegistrations", IncrementalStepRunReason.Modified);
        var generated = Generated(driver);
        Assert.Contains("global::Contracts.SecondFactory.Create(options)", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("global::Contracts.FirstFactory.Create(options)", generated, StringComparison.Ordinal);
    }

    static CSharpParseOptions Options() => new CSharpParseOptions(LanguageVersion.Latest).WithFeatures(
        [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);

    static CSharpCompilation Compilation(CSharpParseOptions options, SyntaxTree unrelated, MetadataReference reference) =>
        CSharpCompilation.Create("ContextIncremental",
            [CSharpSyntaxTree.ParseText(Composition, options, "Composition.cs"), unrelated],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Append(typeof(Aggregate).Assembly.Location)
                .Append(typeof(IRemoteRequestSender).Assembly.Location)
                .Append(typeof(PortiaBuilder).Assembly.Location)
                .Distinct(StringComparer.Ordinal)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .Append(reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    static CSharpGeneratorDriver Driver(CSharpParseOptions options) => CSharpGeneratorDriver.Create(
        [new RegistrationCallInterceptorGenerator().AsSourceGenerator()], parseOptions: options,
        driverOptions: new GeneratorDriverOptions(default, true));

    static MetadataReference ContextReference(string factory) => GeneratorCompilation.ReferenceNamed("JsonContracts", $$"""
        using System;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Text.Json.Serialization.Metadata;
        using Cntryl.Portia;
        [assembly: PortiaJsonRoot(typeof(string), typeof(Contracts.Context), typeof(Contracts.{{factory}}))]
        namespace Contracts;
        public sealed class Context(JsonSerializerOptions options) : JsonSerializerContext(options)
        {
            protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
            public override JsonTypeInfo? GetTypeInfo(Type type) => null;
        }
        public static class {{factory}}
        {
            public static JsonSerializerContext Create(JsonSerializerOptions options) => new Context(options);
        }
        """);

    static string Generated(GeneratorDriver driver)
    {
        var result = driver.GetRunResult();
        Assert.All(result.Results, item => Assert.Null(item.Exception));
        Assert.DoesNotContain(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        return string.Join("\n", result.Results.Single().GeneratedSources
            .OrderBy(file => file.HintName, StringComparer.Ordinal).Select(file => file.SourceText.ToString()));
    }

    static void AssertReasons(GeneratorDriver driver, string name, IncrementalStepRunReason expected)
    {
        var outputs = driver.GetRunResult().Results.Single().TrackedSteps[name]
            .SelectMany(step => step.Outputs).ToArray();
        Assert.NotEmpty(outputs);
        Assert.All(outputs, output => Assert.Equal(expected, output.Reason));
    }
}
