using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

/// <summary>Checks concrete conditional registration calls alongside ordinary and null-receiver controls.</summary>
public sealed class ConditionalRegistrationTests
{
    [Theory]
    [InlineData("PortiaBuilder.AddRequestHandler")]
    public void ShouldAllowRegistrationNameofWithoutRegistration(string member)
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics($$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            public sealed record Command : IRequest;
            public sealed class Handler : IRequestHandler<Command>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Command> context, CancellationToken ct)
                    => ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static string Name(PortiaBuilder app) => nameof({{member}});
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("conditional")]
    [InlineData("null_receiver")]
    public void ShouldRegisterConcreteHandlerGivenConditionalReceiver(string form)
    {
        // Arrange
        var call = form == "ordinary" ? "app!.AddRequestHandler<Handler>();" : "app?.AddRequestHandler<Handler>();";
        var receiver = form == "null_receiver" ? "null" : "new ServiceCollection().AddPortia()";
        var source = $$"""
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Command : IRequest;
            public sealed class Handler : IRequestHandler<Command>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Command> context, CancellationToken ct)
                    => ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static void Run()
                {
                    PortiaBuilder? app = {{receiver}};
                    {{call}}
                }
            }
            """;
        var assembly = GeneratorCompilation.Compile(source, new RegistrationCallInterceptorGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!;

        // Act
        var error = Record.Exception(() => run.Invoke(null, null));

        // Assert
        Assert.Null(error is TargetInvocationException invoked ? invoked.InnerException : error);
    }
}
