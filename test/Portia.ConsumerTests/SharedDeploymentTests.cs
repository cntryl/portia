using System.Security.Claims;
using Cntryl.Fitz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

[Collection("Account application workers")]
[Trait("Category", "BrokerIntegration")]
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
        _ = Shared(apiBuilder.Services, client, effects, id);
        using var api = apiBuilder.Build();
        await api.StartAsync();
        try
        {
            var publisher = api.Services.GetRequiredService<IRequestQueuePublisher>();
            await publisher.EnqueueAsync(new DepositAccount(id, 3), new RequestRouteValues(Resource: id.ToString()),
                null);
            Assert.Empty(effects.Items);
            var workerBuilder = Host.CreateApplicationBuilder();
            _ = Shared(workerBuilder.Services, client, effects, id).AddWorkers().AddWorkers();
            using var worker = workerBuilder.Build();
            await worker.StartAsync();
            try
            {
                await effects.WaitForAsync("business", aggregateId: id);
                await effects.WaitForAsync("first-projector", aggregateId: id);
                await publisher.EnqueueAsync(new DepositAccount(secondId, 4),
                    new RequestRouteValues(Resource: secondId.ToString()), null);
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
            finally
            {
                await worker.StopAsync();
            }
        }
        finally
        {
            await api.StopAsync();
        }

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
            ["Endpoint"] = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws"
        }).Build();
        _ = builder.Services.AddContracts();
        _ = builder.Services.AddPortia().AddFitz(configuration);
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
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task ShouldFailBeforeConnectingGivenMissingActorValidatorWhenStartingDefaultFitzWorkers()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddAccounts();
        _ = builder.Services.AddPortia()
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")))
            .AddWorkers();
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
        _ = builder.Services.AddAccounts();
        _ = builder.Services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.PerTenant)
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), _ => { })
            .AddWorkers();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains(nameof(ITenantDirectory), error.Message, StringComparison.Ordinal);
    }

    static PortiaBuilder Shared(IServiceCollection services, Client client, ConsumerHost.Effects effects, Uuid first)
    {
        foreach (var descriptor in ConsumerHost.CreateServices())
            services.Add(descriptor);
        _ = services.RemoveAll<IEventStore>();
        _ = services.RemoveAll<ConsumerHost.Effects>();
        _ = services.AddSingleton(effects);
        _ = services.AddScoped<IRequestActorValidator, AcceptActor>();
        _ = services.AddAccounts();
        return services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.Global, o =>
            {
                o.Name = "first-projector";
                o.PollInterval = TimeSpan.FromMilliseconds(10);
            })
            .UseFitzClient(client, fitz =>
            {
                _ = fitz.UseFleet(new FleetRunOptions { MembershipSelector = $"lease://app-{first}/members/*" });

                _ = fitz.AddRequestWorkers();
            });
    }

    sealed class AcceptActor : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
            => ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
    }
}
