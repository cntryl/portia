using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

[Collection("Account application workers")]
public sealed class SharedDeploymentTests
{
    readonly RequestDispatchContext _saveContext = new(RequestActor.System);
    [Fact]
    public async Task SharedApplicationStartsListenersOnlyInWorkerAndKeepsExternalClientAlive()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        var id = Uuid.CreateVersion4();
        var secondId = Uuid.CreateVersion4();
        var effects = new ConsumerHost.Effects();
        var apiBuilder = Host.CreateApplicationBuilder();
        _ = Shared(apiBuilder.Services, client, effects, id, secondId);
        using var api = apiBuilder.Build();
        await api.StartAsync();
        try
        {
            var publisher = api.Services.GetRequiredService<IRequestQueuePublisher>();
            await publisher.EnqueueAsync(new DepositAccount(id, 3), new RequestRouteValues(Resource: id.ToString()), null);
            Assert.Empty(effects.Items);
            var workerBuilder = Host.CreateApplicationBuilder();
            _ = Shared(workerBuilder.Services, client, effects, id, secondId).AddWorker().AddWorker();
            using var worker = workerBuilder.Build();
            await worker.StartAsync();
            try
            {
                await effects.WaitForAsync("business", aggregateId: id);
                await effects.WaitForAsync("first-projector", aggregateId: id);
                await publisher.EnqueueAsync(new DepositAccount(secondId, 4), new RequestRouteValues(Resource: secondId.ToString()), null);
                await effects.WaitForAsync("business", aggregateId: secondId);
                var response = await api.Services.GetRequiredService<IRemoteRequestSender>().SendAsync(
                    new DepositAccount(id, 5), new RequestRouteValues(Resource: id.ToString()), null);
                Assert.True(response.IsSuccess);
                await using var scope = api.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
                Assert.Equal(8, (await repository.HydrateAsync(new Account(id))).Balance);
                var store = api.Services.GetRequiredService<IEventStore>();
                Assert.Same(store, api.Services.GetRequiredService<IDomainEventReader>());
                Assert.Same(store, api.Services.GetRequiredService<IDomainEventWriter>());
            }
            finally { await worker.StopAsync(); }
        }
        finally { await api.StopAsync(); }
        // Disposing both hosts must not dispose a supplied application-owned connection.
        api.Dispose();
        _ = await client.Queue.EnqueueAsync($"queue://consumer/business/{Uuid.CreateVersion4()}", new byte[] { 1 });
    }

    [Fact]
    public async Task OwnedConfigurationConnectsBeforeHostStartsAndSupportsPersistence()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws",
        }).Build();
        _ = builder.Services.AddPortia(portia =>
        {
            _ = portia.Services.AddContracts();
            _ = portia.AddFitz(configuration, fitz => fitz.AddEventStore());
        });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            Assert.Same(
                scope.ServiceProvider.GetRequiredService<IEventStore>(),
                scope.ServiceProvider.GetRequiredService<IDomainEventNotifier>());
            var repository = scope.ServiceProvider.GetRequiredService<IAggregateRepository>();
            var account = new Account(Uuid.CreateVersion4());
            account.Deposit(10);
            await repository.SaveAsync(account, _saveContext);
            Assert.Equal(10, (await repository.HydrateAsync(new Account(account.Id))).Balance);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task MissingActorValidatorFailsBeforeAttemptingConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia(portia =>
        {
            _ = portia.Services.AddContracts();
            _ = portia.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz => fitz.AddQueueWorker("queue://setup/work/items"));
        }).AddWorker();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(nameof(IRequestActorValidator), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTenantDirectoryFailsBeforeAttemptingConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);
        _ = builder.Services.AddPortia(portia =>
        {
            _ = portia.Services.AddAccounts();
            _ = portia.AddProjector<FirstProjector>(o => o.PerTenant());
            _ = portia.AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), _ => { });
        }).AddWorker();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(nameof(ITenantDirectory), error.Message, StringComparison.Ordinal);
    }

    static PortiaBuilder Shared(IServiceCollection services, Client client, ConsumerHost.Effects effects, Uuid first, Uuid second)
    {
        foreach (var descriptor in ConsumerHost.CreateServices())
            services.Add(descriptor);
        _ = services.RemoveAll<IEventStore>();
        _ = services.RemoveAll<ConsumerHost.Effects>();
        _ = services.AddSingleton(effects);
        _ = services.AddScoped<IRequestActorValidator, AcceptActor>();
        return services.AddPortia(portia =>
        {
            _ = portia.Services.AddAccounts();
            _ = portia.AddProjector<FirstProjector>(o => { o.Global(); o.Name = "first-projector"; o.PollInterval = TimeSpan.FromMilliseconds(10); });
            _ = portia.UseFitzClient(client, fitz =>
            {
                _ = fitz.UseFleet(new FleetRunOptions { MembershipSelector = $"lease://app-{first}/members/*" });

                _ = fitz.AddEventStore();
                _ = fitz.AddRequestClients();
                _ = fitz.AddRpcServer();
                _ = fitz.AddQueueWorker($"queue://consumer/business/{first}");
                _ = fitz.AddQueueWorker($"queue://consumer/business/{second}");
                _ = fitz.AddQueueWorker($"queue://consumer/business/{first}");
            });
        });
    }

    sealed class AcceptActor : IRequestActorValidator
    {
        public ValueTask<Result<System.Security.Claims.ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
            => ValueTask.FromResult(Result<System.Security.Claims.ClaimsPrincipal>.Success(RequestActor.System));
    }
}
