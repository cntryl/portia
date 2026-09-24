using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

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

    static readonly Assembly[] RoslynAssemblies =
        [typeof(DomainEventCatalogGenerator).Assembly, typeof(RequestHttpBindingGenerator).Assembly];

    static IIncrementalGenerator[] AllGenerators() => Create<IIncrementalGenerator>();

    static DiagnosticAnalyzer[] AllAnalyzers() => Create<DiagnosticAnalyzer>();

    static T[] Create<T>() =>
    [
        .. RoslynAssemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(T).IsAssignableFrom(type) && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => (T)Activator.CreateInstance(type)!)
    ];

    /// <summary>Generators emit source; every diagnostics-only rule is an analyzer.</summary>
    [Fact]
    public void EveryGeneratorAndAnalyzerIsCovered()
    {
        Assert.Equal(6, AllGenerators().Length);
        Assert.Equal(4, AllAnalyzers().Length);
    }

    /// <summary>Every prefix of a realistic file, as it exists while being typed, runs without an exception.</summary>
    [Fact]
    public void NeverThrowOnIncompleteCode()
    {
        var failures = new List<string>();
        for (var length = 0; length <= Corpus.Length; length += 11)
            failures.AddRange(Failures(Corpus[..length]).Select(failure => $"prefix {length}: {failure}"));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>Removing any single line, as an edit in progress does, runs without an exception.</summary>
    [Fact]
    public void NeverThrowWithAnyLineMissing()
    {
        var lines = Corpus.Split('\n');
        var failures = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var source = string.Join('\n', lines.Where((_, line) => line != index));
            failures.AddRange(Failures(source).Select(failure => $"without line {index + 1}: {failure}"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
    }

    /// <summary>Two independent runs over the same input produce identical sources and diagnostics.</summary>
    [Fact]
    public void OutputIsDeterministic()
    {
        var first = Snapshot(Run(Corpus));
        var second = Snapshot(Run(Corpus));

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    static IEnumerable<string> Failures(string source)
    {
        var run = Run(source);
        foreach (var generator in run.Generators.Results.Where(generator => generator.Exception is not null))
            yield return $"{generator.Generator.GetGeneratorType().Name} threw {generator.Exception}";
        foreach (var diagnostic in run.Generators.Diagnostics.Where(diagnostic => diagnostic.Id is "CS8784" or "CS8785"))
            yield return diagnostic.ToString();
        foreach (var exception in run.AnalyzerExceptions)
            yield return exception;
    }

    // Analyzers see the compilation the build sees: user source plus everything the generators emitted.
    static (GeneratorDriverRunResult Generators, ImmutableArray<Diagnostic> Analyzers, List<string> AnalyzerExceptions)
        Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var compilation = CSharpCompilation.Create("Stability",
            [CSharpSyntaxTree.ParseText(source, parseOptions, "Stability.cs")], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create([.. AllGenerators().Select(generator => generator.AsSourceGenerator())],
                parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
        var exceptions = new List<string>();
        var analyzers = generated.WithAnalyzers([.. AllAnalyzers()], new CompilationWithAnalyzersOptions(
                new AnalyzerOptions([]), (exception, analyzer, _) =>
                {
                    lock (exceptions)
                        exceptions.Add($"{analyzer.GetType().Name} threw {exception}");
                }, concurrentAnalysis: true, logAnalyzerExecutionTime: false))
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        return (driver.GetRunResult(), analyzers, exceptions);
    }

    static string[] Snapshot((GeneratorDriverRunResult Generators, ImmutableArray<Diagnostic> Analyzers,
        List<string> AnalyzerExceptions) run) =>
    [
        .. run.Generators.Results.SelectMany(generator => generator.GeneratedSources)
            .Select(source => source.HintName + "\n" + source.SourceText),
        .. run.Generators.Diagnostics.Select(diagnostic => diagnostic.ToString()),
        .. run.Analyzers.Select(diagnostic => diagnostic.ToString()).Order(StringComparer.Ordinal)
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
