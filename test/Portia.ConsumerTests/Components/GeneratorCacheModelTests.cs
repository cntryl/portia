using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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

    [Fact]
    public void SuppressionStateDoesNotInvalidatePortableGeneratorModels()
    {
        const string request = """
                               using Cntryl.Portia;
                               [RequestRoute("*", "orders", "order", "create")]
                               [Discriminator("orders.create")]
                               public sealed record CreateOrder : IRequest, IQueuable;
                               """;
        const string registration = """
                                    using Cntryl.Portia;
                                    using Microsoft.Extensions.DependencyInjection;
                                    public static class Composition
                                    {
                                        public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia();
                                    }
                                    """;
        const string httpBinding = """
                                   using Cntryl.Portia;
                                   using Microsoft.AspNetCore.Routing;
                                   public sealed record GetOrder(int Id) : IRequest, ICallable;
                                   public static class Endpoints
                                   {
                                       public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<GetOrder>("/orders/{id}");
                                   }
                                   """;

        AssertCachedAfterUnrelatedPragmaEdit(
            new PortiaServiceRegistrationGenerator(), "PortiaRequestTransports", request);
        AssertModifiedAfterUnrelatedPragmaEdit(
            new RegistrationCallInterceptorGenerator(), "PortiaRegistrationCalls", registration);
        AssertModifiedAfterUnrelatedPragmaEdit(
            new RequestHttpBindingGenerator(), "PortiaHttpBindings", httpBinding);
    }

    [Fact]
    public void ComponentPracticeFindingsStayCachedAfterUnrelatedEdit()
    {
        const string handler = """
                               using System;
                               using System.Threading;
                               using System.Threading.Tasks;
                               using Cntryl.Portia;
                               public sealed record Request : IRequest;
                               public sealed class Handler(IServiceProvider services) : IRequestHandler<Request>
                               {
                                   public IServiceProvider Services { get; } = services;
                                   public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct) =>
                                       ValueTask.FromResult(Result.Success);
                               }
                               """;

        AssertCachedAfterUnrelatedPragmaEdit(new ComponentPracticeGenerator(), "PortiaComponentPractices", handler);
    }

    static void AssertCachedAfterUnrelatedPragmaEdit(
        IIncrementalGenerator generator,
        string trackingName,
        string body)
    {
        const string pragma = "#pragma warning disable PORTIA020";
        var comment = "//" + new string(' ', pragma.Length - 2);
        Assert.Equal(comment.Length, pragma.Length);
        var original = comment + Environment.NewLine + body;
        var edited = pragma + Environment.NewLine + body;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var tree = CSharpSyntaxTree.ParseText(original, parseOptions, "CacheScenario.cs");
        var compilation = CSharpCompilation.Create("GeneratorCache",
            [tree], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()], parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(default, true));

        driver = driver.RunGenerators(compilation);
        var editedTree = tree.WithChangedText(SourceText.From(edited));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, editedTree));

        var outputs = driver.GetRunResult().Results.Single().TrackedSteps[trackingName]
            .SelectMany(static step => step.Outputs).ToArray();
        var reasons = outputs.Select(static output => output.Reason).ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.True(
            reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
            $"{trackingName} was {reason} after an unrelated equal-width pragma edit."));
    }

    static void AssertModifiedAfterUnrelatedPragmaEdit(
        IIncrementalGenerator generator,
        string trackingName,
        string body)
    {
        const string pragma = "#pragma warning disable PORTIA020";
        var comment = "//" + new string(' ', pragma.Length - 2);
        var original = comment + Environment.NewLine + body;
        var edited = pragma + Environment.NewLine + body;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var tree = CSharpSyntaxTree.ParseText(original, parseOptions, "CacheScenario.cs");
        var compilation = CSharpCompilation.Create("GeneratorCache",
            [tree], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()], parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(default, true));

        driver = driver.RunGenerators(compilation);
        var editedTree = tree.WithChangedText(SourceText.From(edited));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, editedTree));

        var reasons = driver.GetRunResult().Results.Single().TrackedSteps[trackingName]
            .SelectMany(static step => step.Outputs).Select(static output => output.Reason).ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.Equal(IncrementalStepRunReason.Modified, reason));
    }

    static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Aggregate).Assembly.Location)
        .Append(typeof(PortiaBuilder).Assembly.Location)
        .Append(typeof(PortiaHttpBinding).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(static path => MetadataReference.CreateFromFile(path));

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
