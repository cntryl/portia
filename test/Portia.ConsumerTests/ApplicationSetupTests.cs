namespace Cntryl.Portia.Consumer;

public sealed class ApplicationSetupTests
{
    [Fact]
    public async Task ApplicationHydratesCallerConstructedAggregateWithoutFactory()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed class Account(Uuid id) : Aggregate(id, new EventStreamAddress("setup", "accounts", id.ToString()));
            public static class Scenario
            {
                public static async Task Run()
                {
                    var services = new ServiceCollection();
                    services.AddSingleton<IEventStore, InMemoryEventStore>();
                    services.AddPortia(p => { });
                    await using var provider = services.BuildServiceProvider();
                    await using var scope = provider.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
                    var account = new Account(Uuid.CreateVersion4());
                    if (!object.ReferenceEquals(account, await repository.HydrateAsync(account))) throw new System.Exception("Hydration replaced the caller instance");
                }
            }
            """);
        await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task>>()();
    }

    [Fact]
    public void SharedSetupOnlyActivatesWorkersInWorkerHost()
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            public sealed class Worker : IHostedService
            {
                public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
                public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
            }
            public static class Scenario
            {
                static PortiaBuilder Shared(IServiceCollection services) => services.AddPortia(p =>
                    p.ConfigureWorker("test", s => s.AddHostedService<Worker>()));
                public static void Run()
                {
                    var api = new ServiceCollection();
                    Shared(api);
                    using var apiProvider = api.BuildServiceProvider();
                    if (System.Linq.Enumerable.Any(apiProvider.GetServices<IHostedService>())) throw new Exception("API started worker");
                    var worker = new ServiceCollection();
                    Shared(worker).AddWorker().AddWorker();
                    using var workerProvider = worker.BuildServiceProvider();
                    if (System.Linq.Enumerable.Count(System.Linq.Enumerable.OfType<Worker>(workerProvider.GetServices<IHostedService>())) != 1) throw new Exception("Worker not activated exactly once");
                }
            }
            """);
        assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Action>()();
    }
}
