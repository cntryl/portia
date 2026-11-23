using System.Security.Claims;
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
    public async Task ShouldStartListenersOnlyInWorkerGivenSharedApplicationAndKeepExternalClientAlive()
    {
        // Arrange
        await using var client = await ConsumerBroker.ConnectAsync();
        var id = Uuid.CreateVersion4();
        var secondId = Uuid.CreateVersion4();
        var effects = new ConsumerHost.Effects();
        var apiBuilder = Host.CreateApplicationBuilder();
        _ = Shared(apiBuilder.Services, client, effects, id);
        using var api = apiBuilder.Build();

        // Act
        await api.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        try
        {
            var publisher = api.Services.GetRequiredService<IRequestQueuePublisher>();
            await publisher.EnqueueAsync(new DepositAccount(id, 3), new RequestRouteValues(Resource: id.ToString()),
                null, TestContext.Current.CancellationToken);
            Assert.Empty(effects.Items);
            var workerBuilder = Host.CreateApplicationBuilder();
            _ = Shared(workerBuilder.Services, client, effects, id).AddWorkers().AddWorkers();
            using var worker = workerBuilder.Build();
            await worker.StartAsync(TestContext.Current.CancellationToken);

            try
            {
                await effects.WaitForAsync("business", aggregateId: id);
                await effects.WaitForAsync("first-projector", aggregateId: id);
                await publisher.EnqueueAsync(new DepositAccount(secondId, 4),
                    new RequestRouteValues(Resource: secondId.ToString()), null, TestContext.Current.CancellationToken);
                await effects.WaitForAsync("business", aggregateId: secondId);
                var response = await api.Services.GetRequiredService<IRemoteRequestSender>().SendAsync(
                    new DepositAccount(id, 5), new RequestRouteValues(Resource: id.ToString()), null, TestContext.Current.CancellationToken);
                Assert.True(response.IsSuccess);
                await using var scope = api.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.Aggregates();
                Assert.Equal(8, (await repository.HydrateAsync(new Account(id), TestContext.Current.CancellationToken)).Balance);
                var store = api.Services.GetRequiredService<IEventStore>();
                Assert.Same(store, api.Services.GetRequiredService<IDomainEventReader>());
                Assert.Same(store, api.Services.GetRequiredService<IDomainEventWriter>());
            }
            finally
            {
                await worker.StopAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await api.StopAsync(TestContext.Current.CancellationToken);
        }

        // Disposing both hosts must not dispose a supplied application-owned connection.
        api.Dispose();
        _ = await client.Queue.EnqueueAsync($"queue://consumer/business/{Uuid.CreateVersion4()}", new byte[] { 1 }, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ShouldConnectBeforeHostStartsGivenOwnedConfiguration()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoint"] = Environment.GetEnvironmentVariable("FITZ_TEST_ENDPOINT") ?? "ws://127.0.0.1:4090/ws"
        }).Build();
        _ = builder.Services.AddContracts();
        _ = builder.Services.AddPortia().AddFitz(configuration);
        using var host = builder.Build();

        // Act
        await host.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            Assert.Same(
                scope.ServiceProvider.GetRequiredService<IEventStore>(),
                scope.ServiceProvider.GetRequiredService<IDomainEventNotifier>());
            var repository = scope.ServiceProvider.Aggregates();
            var account = new Account(Uuid.CreateVersion4());
            account.Deposit(10);
            await repository.SaveAsync(account, _saveContext, TestContext.Current.CancellationToken);
            Assert.Equal(10, (await repository.HydrateAsync(new Account(account.Id), TestContext.Current.CancellationToken)).Balance);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ShouldFailBeforeConnectingGivenMissingActorValidatorWhenStartingDefaultFitzWorkers()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddAccounts();
        _ = builder.Services.AddPortia()
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz => fitz.UseFleet(
                new FleetRunOptions { MembershipSelector = "lease://validation/actors/*" }))
            .AddWorkers();

        // Act
        using var host = builder.Build();

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(nameof(IRequestActorValidator), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldFailBeforeConnectingGivenMissingTenantDirectory()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();

        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);

        _ = builder.Services.AddAccounts();
        _ = builder.Services.AddPortia()
            .AddProjector<TenantFirstProjector>("first-projector", WorkloadScope.PerTenant)
            .AddFitz(new ClientConfig(new Uri("ws://127.0.0.1:1/ws")), fitz => fitz.UseFleet(
                new FleetRunOptions { MembershipSelector = "lease://validation/tenants/*" }))
            .AddWorkers();

        // Act
        using var host = builder.Build();

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(nameof(ITenantDirectory), error.Message, StringComparison.Ordinal);
    }

    static PortiaBuilder Shared(
        IServiceCollection services,
        FitzClient client,
        ConsumerHost.Effects effects,
        Uuid first
    )
    {
        foreach (var descriptor in ConsumerHost.CreateServices())
            services.Add(descriptor);

        _ = services.RemoveAll<IEventStore>();
        _ = services.RemoveAll<ConsumerHost.Effects>();
        _ = services.AddSingleton(effects);
        _ = services.AddScoped<IRequestActorValidator, AcceptActor>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, IgnoreTerminalRequest>();
        _ = services.AddAccounts();
        return services.AddPortia()
            .AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                o => { o.PollInterval = TimeSpan.FromMilliseconds(10); })
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

    sealed class IgnoreTerminalRequest : IQueuedRequestTerminalHandler
    {
        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
