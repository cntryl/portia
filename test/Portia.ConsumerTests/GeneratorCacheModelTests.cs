using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

public sealed class GeneratorCacheModelTests
{
    [Fact]
    public void IncrementalGeneratorCacheModelsDoNotRetainCompilerObjects()
    {
        var generators = typeof(DomainEventCatalogGenerator).Assembly.GetTypes()
            .Where(type => typeof(IIncrementalGenerator).IsAssignableFrom(type) && !type.IsAbstract)
            .ToArray();
        var cacheModels = generators
            .SelectMany(static type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            .Where(static type => !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToArray();
        var fields = cacheModels.SelectMany(static type => type.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).ToArray();

        Assert.NotEmpty(generators);
        Assert.NotEmpty(cacheModels);
        Assert.NotEmpty(fields);
        Assert.All(fields, field =>
            Assert.False(ContainsCompilerRoot(field.FieldType),
                $"{field.DeclaringType!.Name}.{field.Name} retains {field.FieldType}."));
    }

    [Fact]
    public void DomainEventCatalogCodeGenerationIgnoresDiagnosticLocationChanges()
    {
        const string source = """
                              using Cntryl.Portia;

                              [Discriminator("cache.event", 1)]
                              public sealed record CacheEvent : DomainEvent;
                              """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var originalTree = CSharpSyntaxTree.ParseText(source, parseOptions, "CacheEvent.cs");
        var compilation = CSharpCompilation.Create("GeneratorCache",
            [originalTree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DomainEventCatalogGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(default, true));
        driver = driver.RunGenerators(compilation);

        var movedTree = CSharpSyntaxTree.ParseText(Environment.NewLine + source, parseOptions, "CacheEvent.cs");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(originalTree, movedTree));

        var steps = driver.GetRunResult().Results.Single()
            .TrackedSteps["DomainEventCatalogRegistrations"];
        var reasons = steps.SelectMany(static step => step.Outputs)
            .Select(static output => output.Reason).ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.Equal(IncrementalStepRunReason.Cached, reason));
    }

    static bool ContainsCompilerRoot(Type type) => ContainsCompilerRoot(type, []);

    static bool ContainsCompilerRoot(Type type, HashSet<Type> visited)
    {
        if (typeof(ISymbol).IsAssignableFrom(type)
            || typeof(SyntaxNode).IsAssignableFrom(type)
            || typeof(SemanticModel).IsAssignableFrom(type)
            || typeof(Compilation).IsAssignableFrom(type)
            || typeof(Diagnostic).IsAssignableFrom(type)
            || typeof(Location).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } elementType)
            return ContainsCompilerRoot(elementType, visited);

        if (type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsCompilerRoot(argument, visited)))
            return true;

        return visited.Add(type)
               && type.Assembly == typeof(DomainEventCatalogGenerator).Assembly
               && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .Any(field => ContainsCompilerRoot(field.FieldType, visited));
    }
}
