using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

public sealed class FriendAssemblyGeneratorTests
{
    [Theory]
    [InlineData("Friend.Core", "Friend.Tests")]
    [InlineData("Friend.Core", "Friend_Core")]
    public void GeneratedHelpersDoNotConflictAcrossFriendAssemblies(string sourceAssembly, string friendAssembly)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Append(typeof(PortiaBuilder).Assembly.Location)
            .Append(typeof(PortiaEndpointRouteBuilderExtensions).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            generalDiagnosticOption: ReportDiagnostic.Error);
        var first = CSharpCompilation.Create(sourceAssembly,
            [CSharpSyntaxTree.ParseText($$"""
                using System.Runtime.CompilerServices;
                using Cntryl.Portia;
                using Microsoft.AspNetCore.Routing;
                using Microsoft.Extensions.DependencyInjection;
                [assembly: InternalsVisibleTo("{{friendAssembly}}")]
                namespace Shared;
                [Discriminator("friend.changed", 1)] public sealed record Changed : DomainEvent;
                public sealed record Query(int Id) : IRequest, ICallable;
                public static class Composition
                {
                    public static PortiaBuilder Register(IServiceCollection services) => services.AddPortia();
                    public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<Query>("/items/{id}");
                    public static DomainEventTypeCatalog Catalog(DomainEventTypeCatalog catalog) => catalog.AddPortiaGeneratedDomainEvents();
                }
                """, options, "First.cs")], references, compileOptions);
        first = Generate(first, options);
        using var image = new MemoryStream();
        var emitted = first.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        var second = CSharpCompilation.Create(friendAssembly,
            [CSharpSyntaxTree.ParseText("""
                using Cntryl.Portia;
                using Microsoft.AspNetCore.Routing;
                using Microsoft.Extensions.DependencyInjection;
                namespace Friend.Tests;
                [Discriminator("test.changed", 1)] public sealed record TestChanged : DomainEvent;
                public static class Composition
                {
                    public static PortiaBuilder Register(IServiceCollection services) => services.AddPortia();
                    public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<Shared.Query>("/tests/{id}");
                }
                """, options, "Second.cs")], [.. references, MetadataReference.CreateFromImage(image.ToArray())], compileOptions);
        second = Generate(second, options);
        var diagnostics = second.GetDiagnostics();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CS0436");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        using var secondImage = new MemoryStream();
        var secondEmit = second.Emit(secondImage);
        Assert.True(secondEmit.Success, string.Join(Environment.NewLine, secondEmit.Diagnostics));
    }

    static CSharpCompilation Generate(CSharpCompilation compilation, CSharpParseOptions options)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RegistrationCallInterceptorGenerator().AsSourceGenerator(),
             new DomainEventCatalogGenerator().AsSourceGenerator(),
             new RequestHttpBindingGenerator().AsSourceGenerator()], parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.All(driver.GetRunResult().Results, result =>
        {
            Assert.Null(result.Exception);
            Assert.NotEmpty(result.GeneratedSources);
        });
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return (CSharpCompilation)output;
    }
}
