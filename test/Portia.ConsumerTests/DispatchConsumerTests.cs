namespace Cntryl.Portia.Consumer;

public sealed class DispatchConsumerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchResolvesOnlySelectedHandlersAndSupportsNestedBus(bool nested)
    {
        var handler = nested
            ? "public sealed class OuterHandler(IRequestBus bus) : IRequestHandler<Outer, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<Outer> c, CancellationToken ct) => bus.SendAsync(new Inner(), RequestActor.System, ct); }"
            : "public sealed class OuterHandler : IRequestHandler<Outer, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<Outer> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(42)); }";
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Outer : IRequest<int>;
            public sealed record Inner : IRequest<int>;
            public sealed record Unrelated : IRequest;
            public sealed class InnerHandler : IRequestHandler<Inner, int>
            {
                public ValueTask<Result<int>> HandleAsync(IRequestContext<Inner> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(42));
            }
            public sealed class UnrelatedHandler : IRequestHandler<Unrelated>
            {
                public UnrelatedHandler() => throw new Exception("Unrelated handler constructed");
                public ValueTask<Result> HandleAsync(IRequestContext<Unrelated> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
            }
            public sealed class UnrelatedAuthorizer : IRequestAuthorizer<Unrelated>
            {
                public UnrelatedAuthorizer() => throw new Exception("Unrelated authorizer constructed");
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Unrelated> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) => ValueTask.FromResult(Result.Success);
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
                    var result = await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(new Outer(), RequestActor.System);
                    return result.Value;
                }
            }
            """ + handler, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator(), new PortiaModuleGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(42, await run());
    }
}
