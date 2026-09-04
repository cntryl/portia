using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ModuleConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothModulesContributeDispatchEventsAndContractTransports(bool reverse)
    {
        var services = new ServiceCollection();
        if (reverse)
        {
            _ = services.AddPortiaModule<ReportingModule>();
            _ = services.AddPortiaModule<AccountsModule>();
        }
        else
        {
            _ = services.AddPortiaModule<AccountsModule>();
            _ = services.AddPortiaModule<ReportingModule>();
        }
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        Assert.Equal(11, (await bus.SendAsync(new FeatureOneRequest(10), RequestActor.System)).Value);
        Assert.Equal(12, (await bus.SendAsync(new FeatureTwoRequest(10), RequestActor.System)).Value);
        var transports = provider.GetServices<RequestTransportRegistration>().ToArray();
        Assert.Equal(2, transports.Length);
        var serializer = provider.GetRequiredService<IDomainEventSerializer>();
        var id = Uuid.CreateVersion7();
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
    public void ConflictingModuleRegistrationsFailExplicitlyWithoutPartialRegistration(bool authorizer)
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
            [PortiaModule] public partial class ConflictingModule;
            public static class Scenario
            {
                public static bool Run()
                {
                    var services = new ServiceCollection();
                    services.AddPortiaModule<AccountsModule>();
                    var count = services.Count;
                    try { services.AddPortiaModule<ConflictingModule>(); return false; }
                    catch (InvalidOperationException exception) { return services.Count == count && exception.Message.Contains("conflicting"); }
                }
            }
            """ + component, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator(), new PortiaModuleGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>();
        Assert.True(run());
    }

    [Fact]
    public void ExplicitModulesComposeIdempotentlyThroughPublicApi()
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
                    services.AddPortiaModule<AccountsModule>();
                    services.AddPortiaModule<ReportingModule>();
                    var count = services.Count;
                    services.AddPortiaModule<AccountsModule>();
                    services.AddPortiaModule<ReportingModule>();
                    return services.Count - count;
                }
            }
            """);
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(0, run());
    }
}
