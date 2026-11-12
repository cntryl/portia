using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class JsonMetadataGeneratorTests
{
    [Theory]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, new RequestQueueOptions(TtlHours: 1))")]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, RequestMetadata.Create(), new RequestQueueOptions(TtlHours: 1), default)")]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, parent, new RequestQueueOptions(TtlHours: 1))")]
    public void RequiresRequestJsonMetadataForOptionsPublication(string invocation)
    {
        // Arrange
        var source = $$"""
                       using Cntryl.Portia;
                       [RequestRoute("app", "jobs", "requests", "run")]
                       [Discriminator("test.queued_options")]
                       public sealed record Queued : IRequest, IQueuable;
                       public static class Scenario
                       {
                           public static void Run(IRequestQueuePublisher publisher, IExecutionContext parent)
                           {
                               _ = {{invocation}};
                           }
                       }
                       """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Equal(["Queued"], diagnostics.Where(diagnostic => diagnostic.Id == "PORTIA025")
            .Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]));
    }

    [Fact]
    public void IgnoresUnregisteredHandlersButStillCatalogsDomainEvents()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Equal(["Created"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RegisteredHandlerRequiresRequestAndResultRoots()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Equal(["Answer", "Query"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EndpointAcceptsRootAdvertisedByReferencedAssembly()
    {
        // Arrange
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

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Contracts;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public static class Endpoints
                                                           {
                                                               public static void Map(IEndpointRouteBuilder routes) => routes.MapPortiaPost<Query>("/query");
                                                           }
                                                           """, [contracts], new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void AddPortiaComposesReferencedContextFactory()
    {
        // Arrange
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

        // Act
        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using Cntryl.Portia;
                                                             using Microsoft.Extensions.DependencyInjection;
                                                             public static class App
                                                             {
                                                                 public static void Configure(IServiceCollection services) => services.AddPortia();
                                                             }
                                                             """, [contracts],
            new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("global::Cntryl.Portia.Generated.PortiaJsonContextFactory_", generated,
            StringComparison.Ordinal);
        Assert.Contains(".Create(options)", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolRequiresRequestAndResultRoots()
    {
        // Arrange
        var mcp = MetadataReference.CreateFromFile(
            typeof(PortiaMcpApplicationExtensions).Assembly.Location);

        // Act
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

        // Assert
        Assert.Equal(["Answer", "Query"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AcceptsExplicitRootsAndDoesNotRequireNestedTypes()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void IgnoresConsumerMethodsWhoseNamesMatchPortiaApis()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Id is "PORTIA025" or "CS8785");
    }

    [Fact]
    public void NamedConfigurationBeforePatternUsesTheSemanticRouteArgument()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void BinaryStreamContractsDoNotRequireJsonMetadata()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ReportsEndpointConfigurationTypesWithoutJsonMetadata()
    {
        // Arrange
        const string source = """
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
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Equal(["CreateBody", "CreatedDto", "Tenant"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BodyMembersAreCoveredByTheirRequestRoot()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using System.Text.Json.Serialization;
                                                           using Cntryl.Portia;
                                                           using Microsoft.AspNetCore.Routing;
                                                           public readonly record struct Money(long Cents);
                                                           public sealed record Deposit(Uuid AccountId, long AmountCents, string Memo, Money? Fee, DateTimeOffset? At) : IRequest, ICallable;
                                                           public static class Scenario
                                                           {
                                                               public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Deposit>("/accounts/{account_id}/deposits");
                                                           }
                                                           [PortiaJsonContext]
                                                           [JsonSerializable(typeof(Deposit))]
                                                           internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Theory]
    [InlineData("MapPortiaPost", false)]
    [InlineData("MapPortiaPut", false)]
    [InlineData("MapPortiaPatch", false)]
    [InlineData("MapPortiaPost", true)]
    [InlineData("MapPortiaGet", false)]
    [InlineData("MapPortiaDelete", false)]
    public void ConvertedRequestsRequireIndependentBodyMetadata(string mapping, bool bodyRoot)
    {
        // Arrange
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                            using System;
                                                            using System.Text.Json;
                                                            using System.Text.Json.Serialization;
                                                            using Cntryl.Portia;
                                                            using Microsoft.AspNetCore.Routing;
                                                            public readonly record struct Body(int Value);
                                                            [JsonConverter(typeof(ChangeConverter))]
                                                            public sealed record Change(Uuid Id, Body? Value) : IRequest, ICallable;
                                                            public sealed class ChangeConverter : JsonConverter<Change>
                                                            {
                                                                public override Change Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
                                                                public override void Write(Utf8JsonWriter writer, Change value, JsonSerializerOptions options) => throw new NotSupportedException();
                                                            }
                                                            public static class Scenario
                                                            {
                                                                public static void Map(IEndpointRouteBuilder app) => app.{{mapping}}<Change>("/changes/{id}");
                                                            }
                                                            [PortiaJsonContext]
                                                            [JsonSerializable(typeof(Change))]
                                                            {{(bodyRoot ? "[JsonSerializable(typeof(Body))]" : string.Empty)}}
                                                            internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                            """, new JsonMetadataDiagnosticGenerator());

        // Act
        var required = !bodyRoot && mapping is "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch";

        // Assert
        Assert.Equal(required ? ["Body"] : Array.Empty<string>(), diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/files/{*path}")]
    [InlineData("/files/{**path}")]
    [InlineData("/files/{path=default}")]
    [InlineData("/files/{path:minlength(1)}")]
    public void CatchAllAndDefaultRouteTokensAreNotBodyRoots(string route)
    {
        // Arrange
        var source = $$""""
                                                             using System;
                                                             using System.Text.Json;
                                                             using System.Text.Json.Serialization;
                                                             using Cntryl.Portia;
                                                             using Microsoft.AspNetCore.Routing;
                                                             public readonly record struct FilePath(string Value)
                                                             {
                                                                 public static bool TryParse(string value, out FilePath result) { result = new(value); return true; }
                                                             }
                                                             [JsonConverter(typeof(UploadConverter))]
                                                             public sealed record Upload(FilePath Path, string Name) : IRequest, ICallable;
                                                             public sealed class UploadConverter : JsonConverter<Upload>
                                                             {
                                                                 public override Upload Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
                                                                 public override void Write(Utf8JsonWriter writer, Upload value, JsonSerializerOptions options) => throw new NotSupportedException();
                                                             }
                                                             public static class Scenario
                                                             {
                                                                 public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<Upload>("{{route}}");
                                                             }
                                                             [PortiaJsonContext]
                                                             [JsonSerializable(typeof(Upload))]
                                                             [JsonSerializable(typeof(string))]
                                                             internal sealed partial class AppJsonContext : JsonSerializerContext;
                                                             """";

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    [Fact]
    public void ReportsRequestsDispatchedThroughNullConditionalCalls()
    {
        // Arrange
        const string source = """
                                                           using System.Security.Claims;
                                                           using Cntryl.Portia;
                                                           public sealed record Ping : IRequest;
                                                           public static class Scenario
                                                           {
                                                               public static void Send(IRequestBus? bus) => _ = bus?.SendAsync(new Ping(), new ClaimsPrincipal());
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA025" &&
                                                   diagnostic.GetMessage(CultureInfo.InvariantCulture)
                                                       .Contains("'Ping'"));
    }

    [Fact]
    public void RequiresJsonMetadataForOutcomeAwareRequestAndResultRoots()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           [RequestRoute("test", "generators", "outcome", "only")]
                                                           [Discriminator("outcome.only", 1)]
                                                           public sealed record Ping : IRequest<Pong>, ICallable;
                                                           public sealed record Pong;
                                                           public static class Scenario
                                                           {
                                                               public static void Send(IRemoteRequestSender sender) => _ =
                                                                   sender.SendWithOutcomeAsync<Ping, Pong>(new Ping(),
                                                                       new RequestRouteValues(), null);
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new JsonMetadataDiagnosticGenerator());

        // Assert
        Assert.Equal(["Ping", "Pong"], diagnostics.Where(d => d.Id == "PORTIA025")
            .Select(d => d.GetMessage(CultureInfo.InvariantCulture).Split('\'')[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AcceptsAnUpcasterForAReferencedDomainEventUsedByTheApplication()
    {
        // Arrange
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("external.changed", 2)]
                                                       public sealed record ExternalChanged : DomainEvent;
                                                       """);

        // Act
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

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA012");
    }
}
