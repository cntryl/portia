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
}
