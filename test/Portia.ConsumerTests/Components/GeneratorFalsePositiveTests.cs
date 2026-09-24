using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Valid application code the generators must accept: no diagnostic on it, and generated source that compiles.
///     Each case is a shape a generator once rejected or emitted broken code for.
/// </summary>
public sealed class GeneratorFalsePositiveTests
{
    [Fact]
    public void RegistrationNamesCompileForKeywordNamespaces()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 namespace A.@class
                                                                 {
                                                                     [RequestRoute("a", "b", "c", "d")][Discriminator("p1", 1)]
                                                                     public sealed record Ping : IRequest, ICallable;
                                                                 }
                                                                 namespace B
                                                                 {
                                                                     [RequestRoute("a", "b", "c", "e")][Discriminator("p2", 1)]
                                                                     public sealed record Ping : IRequest, ICallable;
                                                                 }
                                                                 """, new PortiaServiceRegistrationGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void HttpBindingCompilesForLargeDecimalDefaults()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed record Quote(decimal Limit = decimal.MaxValue, decimal Floor = decimal.MinValue) : IRequest<string>, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<Quote, string>("/quotes");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void HttpBindingReportsRequiredMembersInsteadOfEmittingBrokenConstruction()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed class Create : IRequest, ICallable { public required string Name { get; init; } }
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Create>("/things");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void HttpBindingAcceptsRecordStructRequests()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public readonly record struct GetThing(Guid Id) : IRequest<string>, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<GetThing, string>("/things/{id}");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA016");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void HttpBindingAcceptsRegexConstraintsContainingQuestionMarks()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed record GetCode(string Code) : IRequest<string>, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<GetCode, string>("/codes/{code:regex(^[a-z]?$)}");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA026");
    }

    [Fact]
    public void HttpBindingStillRejectsOptionalRouteTokens()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed record GetCode(string Code) : IRequest<string>, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app)
                                                                     {
                                                                         app.MapPortiaGet<GetCode, string>("/a/{code?}");
                                                                         app.MapPortiaGet<GetCode, string>("/b/{code:int?}");
                                                                     }
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "PORTIA026"));
    }

    [Fact]
    public void HttpBindingAcceptsDuplicateMappingExcludedThroughALocal()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Builder;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed record GetA(string Id) : IRequest<string>, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app)
                                                                     {
                                                                         var legacy = app.MapPortiaGet<GetA, string>("/legacy/a/{id}");
                                                                         legacy.ExcludeFromDescription();
                                                                         app.MapPortiaGet<GetA, string>("/a/{id}");
                                                                     }
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA027");
    }

    [Fact]
    public void JsonMetadataIgnoresAbstractAndOpenGenericDispatchArguments()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System;
                                                           using System.Collections.Generic;
                                                           using System.Security.Claims;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public static class Dispatch
                                                           {
                                                               // Guid is the real result root and is registered; IRequest<Guid> is not a root.
                                                               public static ValueTask<Result<Guid>> Go(IRequestBus bus, IRequest<Guid> request, ClaimsPrincipal actor) =>
                                                                   bus.SendAsync(request, actor);
                                                               public static ValueTask<Result<IReadOnlyList<T>>> Page<T>(IRequestBus bus, IRequest<IReadOnlyList<T>> request, ClaimsPrincipal actor) =>
                                                                   bus.SendAsync(request, actor);
                                                           }
                                                           [PortiaJsonContext]
                                                           [System.Text.Json.Serialization.JsonSerializable(typeof(Guid))]
                                                           internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                           """, new JsonMetadataAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void JsonMetadataIgnoresFileLocalEvents()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           [Discriminator("j01", 1)] file sealed record J01 : DomainEvent;
                                                           [PortiaJsonContext]
                                                           internal sealed partial class AppJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
                                                           """, new JsonMetadataAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void ProcessorDerivedFromAPartialDispatchingBaseNeedsNoPartial()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 public sealed record Changed : DomainEvent;
                                                                 public partial class BaseProjection(IProjectionStore store)
                                                                     : Projector(store, EventStreamPattern.ForPattern("events")), IProjectorHandler<Changed>
                                                                 {
                                                                     public ValueTask HandleAsync(Changed ev, IProjectorContext context, CancellationToken ct) => ValueTask.CompletedTask;
                                                                 }
                                                                 public sealed class Projection(IProjectionStore store) : BaseProjection(store);
                                                                 """, new ProjectorReactorEventDispatcherGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA002");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void UpcasterForAnEventDeclaredInAReferencedFeatureIsKnown()
    {
        var feature = GeneratorCompilation.Reference("""
                                                     using Cntryl.Portia;
                                                     namespace FeatureOne;
                                                     [Discriminator("f1.created", 2)]
                                                     public sealed record Created : DomainEvent;
                                                     """);
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           public sealed class Upcaster : IJsonDomainEventUpcaster
                                                           {
                                                               public string EventName => "f1.created";
                                                               public int FromVersion => 1;
                                                               public System.Text.Json.Nodes.JsonObject Upcast(System.Text.Json.Nodes.JsonObject json) => json;
                                                           }
                                                           """, [feature], new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA012");
    }

    [Theory]
    [InlineData("switch (value) { case UsedEvent: return 1; default: return 0; }")]
    [InlineData("return value switch { UsedEvent => 1, _ => 0 };")]
    [InlineData("_ = UsedEvent.Create(); return 0;")]
    public void RegistersReferencedEventsNamedOnlyInPatternsOrStaticCalls(string body)
    {
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("used.event", 1)]
                                                       public sealed record UsedEvent(string Value) : DomainEvent
                                                       {
                                                           public static UsedEvent Create() => new("x");
                                                       }
                                                       """);
        var generated = GeneratorCompilation.GeneratedSource($$"""
                                                              using Cntryl.Portia;
                                                              using Contracts;
                                                              using Microsoft.Extensions.DependencyInjection;
                                                              public static class Composition
                                                              {
                                                                  public static void Add(IServiceCollection services) => services.AddPortia();
                                                                  public static int Classify(object value) { {{body}} }
                                                              }
                                                              """, [contracts], new RegistrationCallInterceptorGenerator());

        Assert.Contains("Contracts.UsedEvent", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void UpcasterSplitAcrossFilesDoesNotCrashTheCatalog()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("SplitUpcaster",
        [
            CSharpSyntaxTree.ParseText("""
                                       using Cntryl.Portia;
                                       [Discriminator("evt", 2)] public sealed record Evt : DomainEvent;
                                       public sealed partial class Up : IJsonDomainEventUpcaster;
                                       """, parseOptions, "a.cs"),
            CSharpSyntaxTree.ParseText("""
                                       using System.Text.Json.Nodes;
                                       public sealed partial class Up
                                       {
                                           public string EventName => "evt";
                                           public int FromVersion => 1;
                                           public JsonObject Upcast(JsonObject payload) => payload;
                                       }
                                       """, parseOptions, "b.cs")
        ], ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location).Append(typeof(PortiaBuilder).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = CSharpGeneratorDriver.Create([new DomainEventCatalogGenerator().AsSourceGenerator()],
            parseOptions: parseOptions).RunGenerators(compilation).GetRunResult();

        Assert.Null(result.Results.Single().Exception);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Id == "PORTIA012");
    }

    /// <summary>A JSON context in a file the compiler treats as generated still covers the roots it declares.</summary>
    [Fact]
    public void JsonContextInAGeneratedFileCoversItsRoots()
    {
        var diagnostics = AnalyzeFiles(new JsonMetadataAnalyzer(),
            ("Events.cs", """
                          using Cntryl.Portia;
                          namespace App;
                          [Discriminator("created", 1)] public sealed record Created : DomainEvent;
                          """),
            ("AppJson.g.cs", """
                             // <auto-generated/>
                             using System.Text.Json.Serialization;
                             using Cntryl.Portia;
                             namespace App;
                             [PortiaJsonContext]
                             [JsonSerializable(typeof(Created))]
                             public partial class AppJson : JsonSerializerContext;
                             """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    /// <summary>A component whose first partial part is generated is still checked through its other parts.</summary>
    [Fact]
    public void ComponentWithAGeneratedFirstPartIsStillChecked()
    {
        var diagnostics = AnalyzeFiles(new ComponentPracticeAnalyzer(),
            ("Handler.designer.cs", """
                                    public sealed partial class Handler
                                    {
                                        public Handler(System.IServiceProvider services) => _ = services;
                                    }
                                    """),
            ("Handler.cs", """
                           using System.Threading;
                           using System.Threading.Tasks;
                           using Cntryl.Portia;
                           public sealed record Ping : IRequest;
                           public sealed partial class Handler : IRequestHandler<Ping>
                           {
                               public ValueTask<Result> HandleAsync(IRequestContext<Ping> context, CancellationToken ct) =>
                                   ValueTask.FromResult(Result.Success);
                           }
                           """));

        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
    }

    static ImmutableArray<Diagnostic> AnalyzeFiles(DiagnosticAnalyzer analyzer, params (string Path, string Source)[] files)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("GeneratedFiles",
            files.Select(file => CSharpSyntaxTree.ParseText(file.Source, parseOptions, file.Path)),
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    static void AssertNoCompilerErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                                                     && diagnostic.Id.StartsWith("CS", StringComparison.Ordinal))
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
