using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ModuleRpcConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleDescriptorsRegisterAllRpcWorkersAndOwnTheirLifetime(bool reverse)
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            public static class Scenario
            {
                public static async Task<IAsyncDisposable> Run(FitzRpcRequestServer server)
                    => await server.RegisterModulesAsync();
            }
            """);
        var register = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<FitzRpcRequestServer, Task<IAsyncDisposable>>>();
        var services = ConsumerHost.CreateServices();
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
        _ = services.AddPortiaModule<AccountsModule>();
        _ = services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = services.AddSingleton<IRequestDeserializer, JsonRequestSerializer>();
        _ = services.AddSingleton<IRequestOutcomeSerializer, JsonRequestSerializer>();
        await using var provider = ConsumerHost.Build(services);
        var rpc = new InMemoryRpcClient();
        var server = new FitzRpcRequestServer(rpc, provider.GetRequiredService<IServiceScopeFactory>());
        var serializer = new JsonRequestSerializer();
        var sender = new FitzRemoteRequestSender(rpc, serializer, serializer);
        await using (await register(server))
        {
            Assert.Equal(11, (await sender.SendAsync<FeatureOneRequest, int>(new(10), new(), null)).Value);
            Assert.Equal(12, (await sender.SendAsync<FeatureTwoRequest, int>(new(10), new(), null)).Value);
        }
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sender.SendAsync<FeatureOneRequest, int>(new(10), new(), null));
    }
}
