namespace Cntryl.Portia.Consumer;

public sealed class GeneratorShapeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryImplementedRequestInterfaceDispatchesAndPartialDeclarationsAreUnique(bool partial)
    {
        var extra = partial ? "public partial class Handler { }" : "";
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record First : IRequest<int>;
            public sealed record Second : IRequest<int>;
            public partial class Handler : IRequestHandler<First, int>, IRequestHandler<Second, int>
            {
                public ValueTask<Result<int>> HandleAsync(IRequestContext<First> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(1));
                public ValueTask<Result<int>> HandleAsync(IRequestContext<Second> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(2));
            }
            public class Authorizer : IRequestAuthorizer<First>, IRequestAuthorizer<Second>
            {
                public static int Calls;
                public ValueTask<Result> AuthorizeAsync(IRequestContext<First> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) { Calls++; return ValueTask.FromResult(Result.Success); }
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Second> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) { Calls++; return ValueTask.FromResult(Result.Success); }
            }
            [PortiaModule] public partial class TestModule;
            public static class Scenario
            {
                public static async Task<int> Run()
                {
                    var services = new ServiceCollection();
                    services.AddPortiaModule<TestModule>();
                    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                    await using var scope = provider.CreateAsyncScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                    return (await bus.SendAsync(new First(), RequestActor.System)).Value + (await bus.SendAsync(new Second(), RequestActor.System)).Value + Authorizer.Calls;
                }
            }
            """ + extra, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator(), new PortiaModuleGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(5, await run());
    }
}
