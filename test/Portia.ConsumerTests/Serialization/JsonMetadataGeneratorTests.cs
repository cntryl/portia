using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class JsonMetadataGeneratorTests
{
    [Fact]
    public void IgnoresUnregisteredHandlersButStillCatalogsDomainEvents()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [Discriminator("created")]
                                                           public sealed record Created : DomainEvent;
                                                           public sealed record Query : IRequest<Answer>;
                                                           public sealed record Answer;
                                                           public sealed class Handler : IRequestHandler<Query, Answer>
                                                           {
                                                               public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                                           }
                                                           public sealed class OtherHandler : IRequestHandler<Query, Answer>
                                                           {
                                                               public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                                           }
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.Equal(["Created"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegisteredHandlerRequiresRequestAndResultRoots()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Microsoft.Extensions.DependencyInjection;
                                                           public sealed record Query : IRequest<Answer>;
                                                           public sealed record Answer;
                                                           public sealed class Handler : IRequestHandler<Query, Answer>
                                                           {
                                                               public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                                           }
                                                           public static class Scenario
                                                           {
                                                               public static void Configure(IServiceCollection services) => services.AddPortia().AddRequestHandler<Handler>();
                                                           }
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.Equal(["Answer", "Query"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EndpointAcceptsRootAdvertisedByReferencedAssembly()
    {
        var contracts = GeneratorCompilation.Reference("""
                                                       using System;
                                                       using System.Text.Json;
                                                       using System.Text.Json.Serialization;
                                                       using System.Text.Json.Serialization.Metadata;
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       public sealed record Query : IRequest, ICallable;
                                                       [PortiaJsonContext]
                                                       [JsonSerializable(typeof(Query))]
                                                       internal sealed class ContractsJsonContext : JsonSerializerContext
                                                       {
                                                           public ContractsJsonContext(JsonSerializerOptions options) : base(options) { }
                                                           protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                                                           public override JsonTypeInfo? GetTypeInfo(Type type) => null;
                                                       }
                                                       """, new JsonRootMarkerGenerator());
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Contracts;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public static class Endpoints
                                                           {
                                                               public static void Map(IEndpointRouteBuilder routes) => routes.MapPortiaPost<Query>("/query");
                                                           }
                                                           """, [contracts], new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void AddPortiaComposesReferencedContextFactory()
    {
        var contracts = GeneratorCompilation.Reference("""
                                                       using System;
                                                       using System.Text.Json;
                                                       using System.Text.Json.Serialization;
                                                       using System.Text.Json.Serialization.Metadata;
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       public sealed record Query : IRequest;
                                                       [PortiaJsonContext]
                                                       [JsonSerializable(typeof(Query))]
                                                       internal sealed class ContractsJsonContext : JsonSerializerContext
                                                       {
                                                           public ContractsJsonContext(JsonSerializerOptions options) : base(options) { }
                                                           protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                                                           public override JsonTypeInfo? GetTypeInfo(Type type) => null;
                                                       }
                                                       """, new JsonRootMarkerGenerator());

        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using Cntryl.Portia;
                                                             using Microsoft.Extensions.DependencyInjection;
                                                             public static class App
                                                             {
                                                                 public static void Configure(IServiceCollection services) => services.AddPortia();
                                                             }
                                                             """, [contracts],
            new RegistrationCallInterceptorGenerator());

        Assert.Contains("_Contracts_ContractsJsonContext.Create(options)", generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolRequiresRequestAndResultRoots()
    {
        var mcp = MetadataReference.CreateFromFile(
            typeof(PortiaMcpApplicationExtensions).Assembly.Location);
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Microsoft.Extensions.DependencyInjection;
                                                           [Discriminator("queries.read")]
                                                           public sealed record Query : IRequest<Answer>, ICallable;
                                                           public sealed record Answer;
                                                           public static class Scenario
                                                           {
                                                               public static void Configure(IServiceCollection services) => services.AddPortia().AddMcpTool<Query>();
                                                           }
                                                           """, [mcp], new JsonMetadataDiagnosticGenerator());

        Assert.Equal(["Answer", "Query"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AcceptsExplicitRootsAndDoesNotRequireNestedTypes()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System.Text.Json.Serialization;
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record Query(Nested Value) : IRequest<Answer>;
                                                           public sealed record Nested;
                                                           public sealed record Answer;
                                                           public sealed class Handler : IRequestHandler<Query, Answer>
                                                           {
                                                               public ValueTask<Result<Answer>> HandleAsync(IRequestContext<Query> context, CancellationToken ct) => default;
                                                           }
                                                           [PortiaJsonContext]
                                                           [JsonSerializable(typeof(Query))]
                                                           [JsonSerializable(typeof(Answer))]
                                                           internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void IgnoresConsumerMethodsWhoseNamesMatchPortiaApis()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           public sealed record Payload;
                                                           public static class ConsumerApis
                                                           {
                                                               public static void AddEvent<T>() { }
                                                               public static void RegisterDynamicRequest<T>() { }
                                                               public static void MapPortiaPost<T>() { }

                                                               public static void Configure()
                                                               {
                                                                   AddEvent<Payload>();
                                                                   RegisterDynamicRequest<Payload>();
                                                                   MapPortiaPost<Payload>();
                                                               }
                                                           }
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Id is "PORTIA025" or "CS8785");
    }

    [Fact]
    public void NamedConfigurationBeforePatternUsesTheSemanticRouteArgument()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System;
                                                           using System.Text.Json.Serialization;
                                                           using Cntryl.Portia;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public readonly record struct RouteId(string Value)
                                                           {
                                                               public static bool TryParse(string value, out RouteId result) { result = new(value); return true; }
                                                           }
                                                           public sealed record Query(RouteId Id, string Value) : IRequest, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Query>(
                                                                   configure: endpoint => endpoint.NoInput().OnBind(_ => new Query(new("id"), "value")),
                                                                   pattern: "/query/{id}");
                                                           }
                                                           [PortiaJsonContext]
                                                           [JsonSerializable(typeof(Query))]
                                                           [JsonSerializable(typeof(string))]
                                                           internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void BinaryStreamContractsDoNotRequireJsonMetadata()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System.IO;
                                                           using System.Text.Json.Serialization;
                                                           using Cntryl.Portia;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record Upload : IRequest, ICallable;
                                                           public sealed class UploadStream : MemoryStream;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Upload>("/upload",
                                                                   endpoint => endpoint
                                                                       .Accepts<UploadStream>("application/octet-stream")
                                                                       .Produces<Stream>(201, "application/octet-stream")
                                                                       .Produces<UploadStream>(202, "application/octet-stream")
                                                                       .OnBind(_ => new Upload()));
                                                           }
                                                           [PortiaJsonContext]
                                                           [JsonSerializable(typeof(Upload))]
                                                           internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ReportsEndpointConfigurationTypesWithoutJsonMetadata()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System.Text.Json.Serialization;
                                                           using Cntryl.Portia;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public sealed record Create : IRequest, ICallable;
                                                           public sealed record CreateBody;
                                                           public sealed record CreatedDto;
                                                           public readonly record struct Tenant(string Value);
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Create>("/create/{tenant}",
                                                                   endpoint => endpoint.Parameter<Tenant>("tenant", PortiaHttpParameterLocation.Route)
                                                                       .Accepts<CreateBody>("application/json")
                                                                       .Produces<CreatedDto>(201)
                                                                       .OnBind(_ => new Create()));
                                                           }
                                                           [PortiaJsonContext]
                                                           [JsonSerializable(typeof(Create))]
                                                           internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.Equal(["CreateBody", "CreatedDto", "Tenant"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/files/{*path}")]
    [InlineData("/files/{**path}")]
    [InlineData("/files/{path=default}")]
    [InlineData("/files/{path:minlength(1)}")]
    public void CatchAllAndDefaultRouteTokensAreNotBodyRoots(string route)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$""""
                                                             using System;
                                                             using System.Text.Json.Serialization;
                                                             using Cntryl.Portia;
                                                             using Microsoft.AspNetCore.Routing;
                                                             public readonly record struct FilePath(string Value)
                                                             {
                                                                 public static bool TryParse(string value, out FilePath result) { result = new(value); return true; }
                                                             }
                                                             public sealed record Upload(FilePath Path, string Name) : IRequest, ICallable;
                                                             public static class Scenario
                                                             {
                                                                 public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Upload>("{{route}}");
                                                             }
                                                             [PortiaJsonContext]
                                                             [JsonSerializable(typeof(Upload))]
                                                             [JsonSerializable(typeof(string))]
                                                             internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                             """", new JsonMetadataDiagnosticGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void ReportsRequestsDispatchedThroughNullConditionalCalls()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System.Security.Claims;
                                                           using Cntryl.Portia;
                                                           public sealed record Ping : IRequest;
                                                           public static class Scenario
                                                           {
                                                               public static void Send(IRequestBus? bus) => _ = bus?.SendAsync(new Ping(), new ClaimsPrincipal());
                                                           }
                                                           """, new JsonMetadataDiagnosticGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA025" &&
                                                   diagnostic.GetMessage(CultureInfo.InvariantCulture)
                                                       .Contains("'Ping'"));
    }

    [Fact]
    public void AcceptsAnUpcasterForAReferencedDomainEventUsedByTheApplication()
    {
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("external.changed", 2)]
                                                       public sealed record ExternalChanged : DomainEvent;
                                                       """);
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Contracts;
                                                           public sealed class Projection
                                                               : Projector(null!, EventStreamPattern.ForPattern("events")),
                                                                 IProjectorHandler<ExternalChanged>;
                                                           public sealed class Upcaster : IJsonDomainEventUpcaster
                                                           {
                                                               public string EventName => "external.changed";
                                                               public int FromVersion => 1;
                                                               public System.Text.Json.Nodes.JsonObject Upcast(System.Text.Json.Nodes.JsonObject json) => json;
                                                           }
                                                           """, [contracts], new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA012");
    }
}
