using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class ConditionalSenderDiscoveryTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("conditional")]
    [InlineData("conditional_legacy")]
    public void ShouldRegisterOutboundQueueRequestGivenConditionalPublication(string form)
    {
        var source = Source(form);
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source,
            new RegistrationCallInterceptorGenerator());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var generated = GeneratorCompilation.GeneratedSource(source,
            new RegistrationCallInterceptorGenerator());

        Assert.Contains("test.conditional_queue", generated, StringComparison.Ordinal);
        Assert.Contains("RequestTransportId(\"queue\")", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRequireRequestJsonMetadataGivenConditionalPublication()
    {
        var diagnostics = GeneratorCompilation.Diagnostics(Source("conditional"),
            new JsonMetadataDiagnosticGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
    }

    static string Source(string form)
    {
        var invocation = form switch
        {
            "conditional" => "if (publisher?.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, new RequestQueueOptions(TtlHours: 1)) is { } pending) await pending;",
            "conditional_legacy" => "if (publisher?.EnqueueAsync(new Queued(), RequestRouteValues.None, null) is { } pending) await pending;",
            _ => "await publisher!.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, new RequestQueueOptions(TtlHours: 1));"
        };
        return $$"""
        #nullable enable
        using System.Threading.Tasks;
        using Cntryl.Portia;
        using Microsoft.Extensions.DependencyInjection;
        [RequestRoute("app", "jobs", "requests", "run")]
        [Discriminator("test.conditional_queue")]
        public sealed record Queued : IRequest, IQueuable;
        public static class Scenario
        {
            public static async Task Run(IServiceCollection services, IRequestQueuePublisher? publisher)
            {
                var builder = services.AddPortia();
                {{invocation}}
            }
        }
        """;
    }
}
