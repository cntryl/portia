using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ExplicitRegistrationConsumerTests
{
    [Fact]
    public void StronglyTypedOutboundCallsInferEveryRequestTransportWithoutExplicitRegistration()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System.Linq;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Microsoft.Extensions.DependencyInjection;
            [RequestRoute("app", "accounts", "*", "deposit")]
            [Discriminator("app.accounts.call")]
            public sealed record CallAccount(int Amount) : IRequest, ICallable;
            [RequestRoute("app", "accounts", "*", "queue")]
            [Discriminator("app.accounts.queue")]
            public sealed record QueueAccount(int Amount) : IRequest, IQueuable;
            [RequestRoute("app", "accounts", "*", "notice")]
            [Discriminator("app.accounts.notice")]
            public sealed record NoticeAccount(int Amount) : IRequest, INotifiable;
            [RequestRoute("app", "accounts", "*", "schedule")]
            [Discriminator("app.accounts.schedule")]
            public sealed record ScheduleAccount(int Amount) : IRequest, ISchedulable;
            public static class Scenario
            {
                public static ValueTask<Result> Call(IRemoteRequestSender sender)
                    => sender.SendAsync(new CallAccount(10), new RequestRouteValues(), null);
                public static ValueTask Queue(IRequestQueuePublisher sender)
                    => sender.EnqueueAsync(new QueueAccount(10), new RequestRouteValues(), null);
                public static ValueTask Notice(INoticeRequestSender sender)
                    => sender.PublishAsync(new NoticeAccount(10), new RequestRouteValues(), null);
                public static ValueTask<string> Schedule(IRequestScheduler sender, RequestScheduleSpec spec)
                    => sender.ScheduleAsync(new ScheduleAccount(10), spec, new RequestRouteValues(), RequestActor.System);

                public static bool Run()
                {
                    var services = new ServiceCollection();
                    services.AddPortia();
                    var requests = services.OfType<ServiceDescriptor>()
                        .Select(descriptor => descriptor.ImplementationInstance)
                        .OfType<RequestTransportRegistration>()
                        .Select(registration => registration.RequestType)
                        .ToArray();
                    return requests.Length == 4
                        && requests.Contains(typeof(CallAccount))
                        && requests.Contains(typeof(QueueAccount))
                        && requests.Contains(typeof(NoticeAccount))
                        && requests.Contains(typeof(ScheduleAccount));
                }
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.True(assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>()());
    }

    [Fact]
    public void SelectedHandlersAndAuthorizersRemainIndependent()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Linq;
            using System.Security.Claims;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Request : IRequest;
            public class Selected : IRequestHandler<Request>, IRequestAuthorizer<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Request> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
            }
            public class Unselected : IRequestHandler<Request>, IRequestAuthorizer<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => throw new Exception();
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Request> c, CancellationToken ct) => throw new Exception();
            }
            public static class Scenario
            {
                public static bool Run()
                {
                    var services = new ServiceCollection();
                    var portia = services.AddPortia().AddRequestHandler<Selected>();
                    if (services.Any(s => s.ServiceType == typeof(RequestAuthorizerRegistration))) return false;
                    portia.AddRequestHandler<Selected>();
                    portia.AddRequestAuthorizer<Selected>();
                    if (!services.Any(s => s.ServiceType == typeof(RequestAuthorizerRegistration))) return false;
                    var count = services.Count;
                    try { portia.AddRequestHandler<Unselected>(); return false; }
                    catch (InvalidOperationException) { }
                    return services.Count == count && !services.Any(s => s.ServiceType == typeof(Unselected));
                }
            }
            """, new RegistrationCallInterceptorGenerator());
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

    [Fact]
    public void ShouldFailWithoutPartialRegistrationGivenConflictingHandlersWhenRegistering()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Cntryl.Portia.Consumer;
            using Microsoft.Extensions.DependencyInjection;
            public static class Scenario
            {
                public static bool Run()
                {
                    var services = new ServiceCollection();
                    services.AddAccounts();
                    var count = services.Count;
                    try { services.AddPortia().AddRequestHandler<Conflict>(); return false; }
                    catch (InvalidOperationException exception) { return services.Count == count && exception.Message.Contains("conflicting"); }
                }
            }
            public class Conflict : IRequestHandler<FeatureOneRequest, int>
            {
                public ValueTask<Result<int>> HandleAsync(IRequestContext<FeatureOneRequest> c, CancellationToken ct)
                    => ValueTask.FromResult(Result<int>.Success(0));
            }
            """, new RegistrationCallInterceptorGenerator(), new PortiaServiceRegistrationGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>();
        Assert.True(run());
    }

    [Fact]
    public void ExplicitFeaturesComposeIdempotentlyThroughPublicApi()
    {
        var assembly = GeneratorCompilation.Compile("""
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
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
