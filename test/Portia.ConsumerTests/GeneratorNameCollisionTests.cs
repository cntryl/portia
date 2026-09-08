namespace Cntryl.Portia.Consumer;

public sealed class StableRegistrationIdentityTests
{
    [Fact]
    public void ShouldCompileGivenSameSimpleComponentNamesWhenStableGenericRegistrationsAreUsed() =>
        GeneratorCompilation.Compile("""
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            namespace Requests
            {
                [RequestRoute("consumer", "collisions", "*", "run")]
                public sealed record Component : IRequest<int>, ICallable;
            }
            namespace Handlers
            {
                public sealed class Component : IRequestHandler<Requests.Component, int>
                {
                    public ValueTask<Result<int>> HandleAsync(IRequestContext<Requests.Component> context, CancellationToken ct) =>
                        ValueTask.FromResult(Result<int>.Success(42));
                }
            }
            namespace Authorizers
            {
                public sealed class Component : IRequestAuthorizer<Requests.Component>
                {
                    public ValueTask<Result> AuthorizeAsync(IRequestContext<Requests.Component> context, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) =>
                        ValueTask.FromResult(Result.Success);
                }
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) => services.AddPortia()
                    .AddRequestHandler<Handlers.Component>()
                    .AddRequestAuthorizer<Authorizers.Component>();
            }
            """, new RegistrationCallInterceptorGenerator());

    [Fact]
    public async Task ShouldDispatchByFullTypeIdentityGivenSameSimpleHandlerNamesWhenRegistered()
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
                    services.AddPortia()
                        .AddRequestHandler<A.Handler>()
                        .AddRequestHandler<B.Handler>();
                    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                    await using var scope = provider.CreateAsyncScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                    return (await bus.SendAsync(new One(), RequestActor.System)).Value + (await bus.SendAsync(new Two(), RequestActor.System)).Value;
                }
            }
            """, new RegistrationCallInterceptorGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(3, await run());
    }
}
