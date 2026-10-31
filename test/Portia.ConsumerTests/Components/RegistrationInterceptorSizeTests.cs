using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class RegistrationInterceptorSizeTests
{
    [Fact]
    public void ShouldBoundCatalogAndSharedRegistrationMethods()
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location).Append(typeof(PortiaBuilder).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("BoundedCatalog", [CSharpSyntaxTree.ParseText(Source(3, 1000), options)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RegistrationCallInterceptorGenerator().AsSourceGenerator(), new DomainEventCatalogGenerator().AsSourceGenerator()],
            parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.DoesNotContain(diagnostics.Concat(output.GetDiagnostics()), item => item.Severity == DiagnosticSeverity.Error);
        var files = driver.GetRunResult().Results.SelectMany(result => result.GeneratedSources).ToArray();
        var methods = files.SelectMany(file => file.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.InRange(method.Body?.Statements.Count ?? 0, 0, 128));
        var text = string.Join("\n", files.Select(file => file.SourceText.ToString()));
        Assert.Equal(1000, text.Split("builder.AddGeneratedEvent<", StringSplitOptions.None).Length - 1);
        Assert.Equal(1000, text.Split("catalog.Register<", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ShouldEmitEventRegistrationsOnceGivenManyCallSites()
    {
        // Arrange
        var source = Source(64, 32);

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Equal(32, generated.Split("builder.AddGeneratedEvent<", StringSplitOptions.None).Length - 1);
        Assert.Equal(129, generated.Split("[global::System.Runtime.CompilerServices.InterceptsLocation(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ShouldGrowAdditivelyGivenMoreEventsAndRegistrations()
    {
        // Arrange
        var small = Source(16, 16);
        var large = Source(64, 64);

        // Act
        var smallOutput = GeneratorCompilation.GeneratedSource(small, new RegistrationCallInterceptorGenerator());
        var largeOutput = GeneratorCompilation.GeneratedSource(large, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.True(largeOutput.Length < smallOutput.Length * 5,
            $"Generated source grew from {smallOutput.Length} to {largeOutput.Length} characters when both input dimensions quadrupled.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRegisterEventsForEachBuilderGivenApplicationOrFeatureEntry(bool featureOnly)
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile(Source(3, 4), new RegistrationCallInterceptorGenerator());
        var composition = assembly.GetType("Composition")!;
        var method = composition.GetMethod(featureOnly ? "Feature" : "Compose0")!;

        // Act
        foreach (var services in new[] { new ServiceCollection(), new ServiceCollection() })
        {
            _ = method.Invoke(null, [featureOnly ? services.AddPortia() : services]);

            // Assert
            using var provider = services.BuildServiceProvider();
            var catalog = provider.GetRequiredService<DomainEventTypeCatalog>();
            for (var index = 0; index < 4; index++)
                Assert.Equal(($"size.event_{index}", 1), catalog.Describe(assembly.GetType("Changed" + index)!));
        }
    }

    static string Source(int callSites, int events)
    {
        var source = new StringBuilder("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Request : IRequest;
            public sealed class Handler : IRequestHandler<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
            }
            """);
        for (var index = 0; index < events; index++)
            _ = source.Append(CultureInfo.InvariantCulture, $"\n[Discriminator(\"size.event_{index}\", 1)] public sealed record Changed{index} : DomainEvent;");
        _ = source.AppendLine("public static class Composition {");
        for (var index = 0; index < callSites; index++)
            _ = source.AppendLine(CultureInfo.InvariantCulture, $"public static PortiaBuilder Compose{index}(IServiceCollection services) => services.AddPortia().AddRequestHandler<Handler>();");
        return source.AppendLine("public static PortiaBuilder Feature(PortiaBuilder builder) => builder.AddRequestHandler<Handler>(); }").ToString();
    }
}
