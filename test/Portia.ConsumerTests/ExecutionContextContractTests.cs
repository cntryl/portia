namespace Cntryl.Portia.Consumer;

public sealed class ExecutionContextContractTests
{
    [Fact]
    public async Task GeneratedRequestHandlerReceivesExactRequestAndCancellationToken()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Probe : IRequest;
            public sealed class Capture { public Probe? Expected; public CancellationToken Token; }
            public sealed class Handler(Capture capture) : IRequestHandler<Probe>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Probe> context, CancellationToken ct)
                {
                    if (!ReferenceEquals(context.Request, capture.Expected) || ct != capture.Token)
                        throw new Exception("Request or cancellation context was replaced");
                    return ValueTask.FromResult(Result.Success);
                }
            }
            public static class Scenario
            {
                public static async Task Run()
                {
                    var services = new ServiceCollection();
                    services.AddSingleton<Capture>();
                    services.AddPortia().AddRequestHandler<Handler>();
                    await using var provider = services.BuildServiceProvider();
                    await using var scope = provider.CreateAsyncScope();
                    using var cancellation = new CancellationTokenSource();
                    var request = new Probe();
                    var capture = scope.ServiceProvider.GetRequiredService<Capture>();
                    capture.Expected = request;
                    capture.Token = cancellation.Token;
                    await scope.ServiceProvider.GetRequiredService<IRequestBus>()
                        .SendAsync(request, RequestActor.System, cancellation.Token);
                }
            }
            """, new RegistrationCallInterceptorGenerator());
        await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task>>()();
    }

    [Fact]
    public async Task NormalHandlerSaveStampsDurableAttribution()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Cntryl.Portia.Consumer;
            using Microsoft.Extensions.DependencyInjection;
            public static class Scenario
            {
                public static async Task Run()
                {
                    var services = new ServiceCollection();
                    services.AddSingleton<IEventStore, InMemoryEventStore>();
                    services.AddPortia();
                    await using var provider = services.BuildServiceProvider();
                    await using var scope = provider.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
                    var id = Uuid.CreateVersion4();
                    var context = new RequestContext<DepositAccount>(new DepositAccount(id, 10), RequestActor.System);
                    var account = await repository.HydrateAsync(new Account(id));
                    account.Deposit(10);
                    await repository.SaveAsync(account, context);
                    await foreach (var record in provider.GetRequiredService<IEventStore>().ReadAsync(account.Stream))
                    {
                        var metadata = record.Event.Metadata;
                        if (metadata.CorrelationId != context.CorrelationId || metadata.CausationId != context.RequestId
                            || metadata.ExecutionId != context.ExecutionId || metadata.Actor?.Subject != "portia:system")
                            throw new Exception("Save did not stamp the execution context");
                    }
                }
            }
            """);
        await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task>>()();
    }

    [Fact]
    public async Task NestedDispatchPreservesActorAndCorrelationWithoutSharingExecutionIdentity()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Outer : IRequest;
            public sealed record Inner : IRequest;
            public sealed class Capture { public IRequestContext<Outer>? Parent; }
            public sealed class OuterHandler(IRequestBus bus, Capture capture) : IRequestHandler<Outer>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Outer> context, CancellationToken ct)
                { capture.Parent = context; return bus.SendAsync(new Inner(), context, ct); }
            }
            public sealed class InnerHandler(Capture capture) : IRequestHandler<Inner>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Inner> context, CancellationToken ct)
                {
                    var parent = capture.Parent!;
                    if (context.RequestId == parent.RequestId || context.ExecutionId == parent.ExecutionId
                        || context.CorrelationId != parent.CorrelationId || context.CausationId != parent.RequestId
                        || context.Actor.Identity?.Name != parent.Actor.Identity?.Name
                        || context.Invocation is not DirectInvocation)
                        throw new Exception("Incorrect child context");
                    return ValueTask.FromResult(Result.Success);
                }
            }
            public static class Scenario
            {
                public static async Task Run()
                {
                    var services = new ServiceCollection();
                    services.AddScoped<Capture>();
                    services.AddPortia()
                        .AddRequestHandler<OuterHandler>()
                        .AddRequestHandler<InnerHandler>();
                    await using var provider = services.BuildServiceProvider();
                    await using var scope = provider.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(new Outer(), RequestActor.System);
                }
            }
            """, new RegistrationCallInterceptorGenerator());
        await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task>>()();
    }
}
