using System.Globalization;

namespace Cntryl.Portia.Consumer;

public sealed class JsonMetadataGeneratorTests
{
    [Fact]
    public void ReportsRequestResultAndEventRootsOnce()
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

        Assert.Equal(["Answer", "Created", "Query"], diagnostics.Where(d => d.Id == "PORTIA025")
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
