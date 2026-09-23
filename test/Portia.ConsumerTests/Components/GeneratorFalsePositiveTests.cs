using Microsoft.CodeAnalysis;

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
                                                           """, new JsonMetadataDiagnosticGenerator());

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
                                                           """, new JsonMetadataDiagnosticGenerator());

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

    static void AssertNoCompilerErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                                                     && diagnostic.Id.StartsWith("CS", StringComparison.Ordinal))
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
