using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class EscapedPermissionIdentifierTests
{
    [Theory]
    [InlineData("Id")]
    [InlineData("event")]
    [InlineData("class")]
    public void ShouldCompilePermissionGivenEscapedRequestProperty(string property)
    {
        var permission = "objects/{" + property + "}";
        var source = $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            [RequiresPermission("{{permission}}")]
            public sealed record Read(int @{{property}}) : IRequest;
            public sealed class Handler : IRequestHandler<Read>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Read> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static PortiaBuilder Register(IServiceCollection services) =>
                    services.AddPortia().AddRequestHandler<Handler>();
            }
            """;
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source,
            new RegistrationCallInterceptorGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
