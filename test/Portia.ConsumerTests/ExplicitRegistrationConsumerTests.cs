using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ExplicitRegistrationConsumerTests
{
    [Fact]
    public void UnselectedConflictingHandlersAndAuthorizersAreNotRegistered()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Linq;
            using System.Security.Claims;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Request : IRequest;
            public class Selected : IRequestHandler<Request>, IRequestAuthorizer<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Request> c, ClaimsPrincipal actor, CancellationToken ct = default) => ValueTask.FromResult(Result.Success);
            }
            public class Unselected : IRequestHandler<Request>, IRequestAuthorizer<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => throw new Exception();
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Request> c, ClaimsPrincipal actor, CancellationToken ct = default) => throw new Exception();
            }
            public static class Scenario
            {
                public static bool Run()
                {
                    var services = new ServiceCollection();
                    var portia = services.AddPortia(p => p.AddSelectedHandler());
                    if (services.Any(s => s.ServiceType == typeof(RequestAuthorizerRegistration))) return false;
                    portia.AddSelectedHandler();
                    var count = services.Count;
                    try { portia.AddUnselectedHandler(); return false; }
                    catch (InvalidOperationException) { }
                    return services.Count == count && !services.Any(s => s.ServiceType == typeof(Unselected));
                }
            }
            """, new RequestBusGenerator());
        Assert.True(assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>()());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothFeaturesContributeDispatchEventsAndContractTransports(bool reverse)
    {
        var services = ConsumerHost.CreateServices();
        if (reverse)
        {
            _ = services.AddReporting();
            _ = services.AddAccounts();
        }
        else
        {
            _ = services.AddAccounts();
            _ = services.AddReporting();
        }
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        Assert.Equal(11, (await bus.SendAsync(new FeatureOneRequest(10), RequestActor.System)).Value);
        Assert.Equal(12, (await bus.SendAsync(new FeatureTwoRequest(10), RequestActor.System)).Value);
        var transports = provider.GetServices<RequestTransportRegistration>().ToArray();
        Assert.Equal(4, transports.Length);
        Assert.Contains(transports, registration => registration.RequestType == typeof(FeatureOneRequest));
        Assert.Contains(transports, registration => registration.RequestType == typeof(FeatureTwoRequest));
        var serializer = provider.GetRequiredService<IDomainEventSerializer>();
        var id = Uuid.CreateVersion4();
        DomainEvent[] events = [new Deposited(1), new FeatureOneObserved(2), new FeatureTwoObserved(3)];
        foreach (var ev in events)
        {
            _ = DomainEventSeed.Attach(ev, id, 1);
            Assert.Equal(ev.GetType(), serializer.Deserialize(serializer.Serialize(ev)).GetType());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingComponentRegistrationsFailExplicitlyWithoutPartialRegistration(bool authorizer)
    {
        var component = authorizer
            ? "public class Conflict : IRequestAuthorizer<FeatureOneRequest> { public ValueTask<Result> AuthorizeAsync(IRequestContext<FeatureOneRequest> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) => ValueTask.FromResult(Result.Success); }"
            : "public class Conflict : IRequestHandler<FeatureOneRequest, int> { public ValueTask<Result<int>> HandleAsync(IRequestContext<FeatureOneRequest> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(0)); }";
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Consumer;
            using Microsoft.Extensions.DependencyInjection;
            public static class Scenario
            {
                public static bool Run()
                {
                    var services = new ServiceCollection();
                    services.AddAccounts();
                    var count = services.Count;
                    try { services.AddPortia(p => p.AddConflict()); return false; }
                    catch (InvalidOperationException exception) { return services.Count == count && exception.Message.Contains("conflicting"); }
                }
            }
            """ + component, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>();
        Assert.True(run());
    }

    [Fact]
    public void ExplicitFeaturesComposeIdempotentlyThroughPublicApi()
    {
        var assembly = GeneratorCompilation.Compile("""
            using Cntryl.Portia;
            using Cntryl.Portia.Consumer;
            using Microsoft.Extensions.DependencyInjection;
            public static class Scenario
            {
                public static int Run()
                {
                    var services = new ServiceCollection();
                    services.AddAccounts();
                    services.AddReporting();
                    var count = services.Count;
                    services.AddAccounts();
                    services.AddReporting();
                    return services.Count - count;
                }
            }
            """);
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(0, run());
    }
}
