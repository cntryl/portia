namespace Cntryl.Portia.Consumer;

public sealed class GeneratorNameCollisionTests
{
    [Fact]
    public async Task SameSimpleHandlerNamesCompileAndDispatchByFullTypeIdentity()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record One : IRequest<int>;
            public sealed record Two : IRequest<int>;
            namespace A { public sealed class Handler : IRequestHandler<One, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<One> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(1)); } }
            namespace B { public sealed class Handler : IRequestHandler<Two, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<Two> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(2)); } }
            public static class Scenario
            {
                public static async Task<int> Run()
                {
                    var services = new ServiceCollection();
                    services.AddPortia(p => p.AddHandler<A.Handler>().AddHandler<B.Handler>());
                    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                    await using var scope = provider.CreateAsyncScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                    return (await bus.SendAsync(new One(), RequestActor.System)).Value + (await bus.SendAsync(new Two(), RequestActor.System)).Value;
                }
            }
            """, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(3, await run());
    }
}
