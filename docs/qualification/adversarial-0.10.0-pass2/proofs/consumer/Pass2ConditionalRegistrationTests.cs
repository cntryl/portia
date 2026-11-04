using System.Reflection;

namespace Cntryl.Portia.Consumer;

/// <summary>Checks concrete conditional registration calls alongside ordinary and null-receiver controls.</summary>
public sealed class Pass2ConditionalRegistrationTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("conditional")]
    [InlineData("null_receiver")]
    public void ShouldRegisterConcreteHandlerGivenConditionalReceiver(string form)
    {
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

        var error = Record.Exception(() => run.Invoke(null, null));

        Assert.Null(error is TargetInvocationException invoked ? invoked.InnerException : error);
    }
}
