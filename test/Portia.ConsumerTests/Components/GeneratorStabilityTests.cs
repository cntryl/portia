using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Every Portia generator and analyzer runs on each keystroke in the IDE, so it must never throw on
///     half-written code and must produce the same output for the same input.
/// </summary>
public sealed class GeneratorStabilityTests
{
    const string Corpus = """
                          using System;
                          using System.Collections.Generic;
                          using System.Security.Claims;
                          using System.Threading;
                          using System.Threading.Tasks;
                          using Cntryl.Portia;
                          using Cntryl.Portia.Testing;
                          using Microsoft.AspNetCore.Routing;
                          using Microsoft.Extensions.DependencyInjection;
                          namespace App.@event;
                          [Discriminator("app.changed", 2)]
                          public sealed record Changed(int Value) : DomainEvent;
                          public sealed class Account(Uuid id) : Aggregate(id, new EventStreamAddress("r", "a", id.ToString()));
                          [RequestRoute("*", "app", "thing", "get")][Discriminator("app.get", 1)]
                          public sealed record GetThing(Guid Id, decimal Limit = 1.5m) : IRequest<List<Changed>>, ICallable;
                          public sealed class GetThingHandler : IRequestHandler<GetThing, List<Changed>>
                          {
                              public async ValueTask<Result<List<Changed>>> HandleAsync(IRequestContext<GetThing> context, CancellationToken ct)
                              {
                                  try { await Task.Yield(); return Result<List<Changed>>.Success([]); }
                                  catch (Exception) { return Result<List<Changed>>.Failure(new(RequestErrorKind.Conflict, "x")); }
                              }
                          }
                          public sealed class ThingGuard(IRequestBus bus) : IRequestGuard<GetThing>
                          {
                              public ValueTask<Result> GuardAsync(IRequestContext<GetThing> context, CancellationToken ct) => default;
                          }
                          public static partial class Outer
                          {
                              internal sealed partial class View(IProjectionStore store)
                                  : Projector(store, EventStreamPattern.ForTenant("things")), IProjectorHandler<Changed>
                              {
                                  public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                              }
                          }
                          public sealed partial class Mailer(IRequestBus bus, IProjectionCheckpointStore checkpoints)
                              : BatchReactor(checkpoints, EventStreamPattern.ForPattern("r")), IBatchReactorHandler<Changed>
                          {
                              public ValueTask HandleAsync(IReadOnlyList<IReactorContext<Changed>> contexts, CancellationToken ct) => ValueTask.CompletedTask;
                          }
                          public sealed class Upcaster : IJsonDomainEventUpcaster
                          {
                              public string EventName => "app.changed";
                              public int FromVersion => 1;
                              public System.Text.Json.Nodes.JsonObject Upcast(System.Text.Json.Nodes.JsonObject json) => json;
                          }
                          [PortiaJsonContext]
                          [System.Text.Json.Serialization.JsonSerializable(typeof(GetThing))]
                          internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                          public static class Composition
                          {
                              public static void Add(IServiceCollection services) =>
                                  services.AddPortia().AddRequestHandler<GetThingHandler>().AddProjector<Outer.View>("view", WorkloadScope.PerTenant);
                              public static void Map(IEndpointRouteBuilder app)
                              {
                                  var legacy = app.MapPortiaGet<GetThing, List<Changed>>("/things/{id:regex(^a?$)}");
                                  legacy.ExcludeFromDescription();
                                  app.MapPortiaGet<GetThing, List<Changed>>("/v2/things/{id}");
                              }
                              public static async Task Probe(IServiceProvider services, IRequestBus bus, ClaimsPrincipal actor)
                              {
                                  RequestScenario.For(services).When(new GetThing(Guid.NewGuid())).ExpectHandled();
                                  await RequestScenario.For(services).When(new GetThing(Guid.NewGuid())).ExpectSuccess();
                                  _ = await bus.SendAsync(new GetThing(Guid.NewGuid()), actor);
                              }
                          }
                          """;

    static IIncrementalGenerator[] AllGenerators() =>
        [.. new[]
            {
                typeof(DomainEventCatalogGenerator).Assembly, typeof(ComponentPracticeGenerator).Assembly,
                typeof(RequestHttpBindingGenerator).Assembly
            }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IIncrementalGenerator).IsAssignableFrom(type) && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (IIncrementalGenerator)Activator.CreateInstance(type)!)];

    [Fact]
    public void EveryGeneratorIsCovered() =>
        Assert.Equal(10, AllGenerators().Length);

    /// <summary>Every prefix of a realistic file, as it exists while being typed, runs without a generator exception.</summary>
    [Fact]
    public void GeneratorsNeverThrowOnIncompleteCode()
    {
        var generators = AllGenerators();
        var failures = new List<string>();
        for (var length = 0; length <= Corpus.Length; length += 11)
            failures.AddRange(Failures(Corpus[..length], generators).Select(failure => $"prefix {length}: {failure}"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>Removing any single line, as an edit in progress does, runs without a generator exception.</summary>
    [Fact]
    public void GeneratorsNeverThrowWithAnyLineMissing()
    {
        var generators = AllGenerators();
        var lines = Corpus.Split('\n');
        var failures = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var source = string.Join('\n', lines.Where((_, line) => line != index));
            failures.AddRange(Failures(source, generators).Select(failure => $"without line {index + 1}: {failure}"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>Two independent runs over the same input produce identical sources and diagnostics.</summary>
    [Fact]
    public void GeneratorOutputIsDeterministic()
    {
        var first = Snapshot(Run(Corpus, AllGenerators()));
        var second = Snapshot(Run(Corpus, AllGenerators()));

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AnalyzerCacheModelsDoNotRetainCompilerObjects()
    {
        var fields = typeof(ComponentPracticeGenerator).Assembly.GetTypes()
            .Where(type => typeof(IIncrementalGenerator).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(static type => type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            .Where(static type => !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .SelectMany(static type => type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                      BindingFlags.NonPublic))
            .ToArray();

        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.False(
            typeof(ISymbol).IsAssignableFrom(field.FieldType) || typeof(SyntaxNode).IsAssignableFrom(field.FieldType)
            || typeof(SemanticModel).IsAssignableFrom(field.FieldType)
            || typeof(Compilation).IsAssignableFrom(field.FieldType)
            || typeof(Location).IsAssignableFrom(field.FieldType),
            $"{field.DeclaringType!.Name}.{field.Name} retains {field.FieldType}."));
    }

    static IEnumerable<string> Failures(string source, IIncrementalGenerator[] generators)
    {
        var result = Run(source, generators);
        foreach (var generator in result.Results.Where(generator => generator.Exception is not null))
            yield return $"{generator.Generator.GetGeneratorType().Name} threw {generator.Exception}";
        foreach (var diagnostic in result.Diagnostics.Where(diagnostic => diagnostic.Id is "CS8784" or "CS8785"))
            yield return diagnostic.ToString();
    }

    static GeneratorDriverRunResult Run(string source, IIncrementalGenerator[] generators)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var compilation = CSharpCompilation.Create("Stability",
            [CSharpSyntaxTree.ParseText(source, parseOptions, "Stability.cs")], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver.Create([.. generators.Select(generator => generator.AsSourceGenerator())],
                parseOptions: parseOptions)
            .RunGenerators(compilation).GetRunResult();
    }

    static string[] Snapshot(GeneratorDriverRunResult result) =>
    [
        .. result.Results.SelectMany(generator => generator.GeneratedSources)
            .Select(source => source.HintName + "\n" + source.SourceText),
        .. result.Diagnostics.Select(diagnostic => diagnostic.ToString())
    ];

    static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Aggregate).Assembly.Location)
        .Append(typeof(PortiaBuilder).Assembly.Location)
        .Append(typeof(PortiaHttpBinding).Assembly.Location)
        .Append(typeof(RequestScenario).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(static path => MetadataReference.CreateFromFile(path));
}
