using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

[Collection("Account application workers")]
public sealed class CompleteWorkflowTests
{
    static readonly int[] Deposits = [5, 7, 11, 13];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameBusinessHandlerPersistsAcrossDirectHttpRpcAndQueueWithIndependentAudits(bool fitzStore)
    {
        var endpoints = GeneratorCompilation.Compile("""
            using Cntryl.Portia;
            using Cntryl.Portia.Consumer;
            using Microsoft.AspNetCore.Routing;
            public static class Endpoints
            {
                public static void Map(IEndpointRouteBuilder app) => app.MapPortiaPost<DepositAccount>("/accounts/{id}");
            }
            """, new RequestHttpBindingGenerator());
        var map = endpoints.GetType("Endpoints")!.GetMethod("Map")!.CreateDelegate<Action<IEndpointRouteBuilder>>();
        await using var client = await ConsumerBroker.ConnectAsync();
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
        foreach (var descriptor in ConsumerHost.CreateServices())
            builder.Services.Add(descriptor);
        _ = builder.Services.AddReporting();
        _ = builder.Services.AddAccounts();
        _ = builder.Services.AddPortia();
        if (fitzStore)
            _ = builder.Services.AddSingleton<IEventStore>(provider => new FitzEventStore(client.Stream, provider.GetRequiredService<IDomainEventSerializer>()));
        _ = builder.Services.AddScoped<IRequestActorValidator, DeliveryScopeTests.ScopeValidator>();
        _ = builder.Services.AddSingleton<IRequestDeserializer, JsonRequestSerializer>();
        _ = builder.Services.AddSingleton<IRequestOutcomeSerializer, JsonRequestSerializer>();
        _ = builder.Services.AddSingleton<IRequestQueuePublisher>(new FitzRequestQueuePublisher(client.Queue, new JsonRequestSerializer()));
        // One worker service runs every declared projector and reactor.
        _ = builder.Services.AddPortia()
            .AddProjector<FirstProjector>(WorkloadScope.Global)
            .AddProjector<SecondProjector>(WorkloadScope.Global)
            .AddReactor<FirstReactor>(WorkloadScope.Global)
            .AddReactor<SecondReactor>(WorkloadScope.Global)
            .AddWorkers();
        var id = Uuid.CreateVersion4();
        var route = new RequestRouteValues(Resource: id.ToString());
        _ = builder.Services.AddSingleton<IRequestQueueConsumer>(new FitzRequestQueueConsumer(client.Queue,
            new JsonRequestSerializer(), "queue://consumer/business/" + id));
        _ = builder.Services.AddPortiaQueueRunner();
        await using var app = builder.Build();
        map(app);
        await app.StartAsync();
        var effects = app.Services.GetRequiredService<ConsumerHost.Effects>();
        try
        {
            await using (var scope = app.Services.CreateAsyncScope())
                Assert.True((await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(new DepositAccount(id, 5), RequestActor.System)).IsSuccess);
            using var http = app.GetTestClient();
            using var response = await http.PostAsJsonAsync("/accounts/" + id, new { amount = 7 });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var serializer = new JsonRequestSerializer();
            var server = new FitzRpcRequestServer(client.Rpc, app.Services.GetRequiredService<IServiceScopeFactory>());
            await using var registrations = await server.RegisterRequestsAsync();
            var sender = new FitzRemoteRequestSender(client.Rpc, serializer, serializer);
            Assert.True((await sender.SendAsync(new DepositAccount(id, 11), route, null)).IsSuccess);
            var publisher = app.Services.GetRequiredService<IRequestQueuePublisher>();
            await publisher.EnqueueAsync(new DepositAccount(id, 13), route, null);
            await effects.WaitForAsync("business", 4, id);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var declined = await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(new DepositAccount(id, -1), RequestActor.System);
                Assert.False(declined.IsSuccess);
                var account = await scope.ServiceProvider.GetRequiredService<IAggregateRepository>().HydrateAsync(new Account(id));
                Assert.NotNull(account);
                Assert.Equal(36, account.Balance);
                Assert.Equal(4UL, account.Version);
                Assert.Equal(4UL, account.CommittedStreamPosition);
                var records = new List<DomainEventRecord>();
                await foreach (var record in scope.ServiceProvider.GetRequiredService<IEventStore>().ReadAsync(EventStreamPattern.ForPattern("consumer", "accounts")))
                {
                    if (record.Ev.Metadata.AggregateId == id)
                        records.Add(record);
                }
                Assert.Equal(5, records.Count);
                Assert.All(records, record =>
                {
                    Assert.NotNull(record.Ev.Metadata.Actor);
                    _ = Assert.NotNull(record.Ev.Metadata.ExecutionId);
                    _ = Assert.NotNull(record.Ev.Metadata.CausationId);
                    _ = Assert.NotNull(record.Ev.Metadata.CorrelationId);
                });
                var audit = Assert.Single(records, record => record.Ev.Metadata.IsAudit);
                Assert.NotEqual(account.Stream, audit.Stream);
                Assert.Equal('4', audit.Stream.Resource[14]);
                Assert.Equal(4UL, audit.Ev.Metadata.AggregateVersion);
                Assert.Equal(0UL, audit.ResourceOffset);
                _ = Assert.IsType<Declined>(audit.Ev);
                Assert.Equal(Deposits, records.Where(record => !record.Ev.Metadata.IsAudit)
                    .Select(record => Assert.IsType<Deposited>(record.Ev).Amount));
                Assert.All(records.Where(record => !record.Ev.Metadata.IsAudit), record => Assert.Equal(account.Stream, record.Stream));
            }
            foreach (var component in new[] { "first-projector", "second-projector", "first-reactor", "second-reactor" })
            {
                await effects.WaitForAsync(component, 5, id);
                var applied = effects.Items.Where(item => item.Component == component && item.AggregateId == id).ToArray();
                Assert.Equal(5, applied.Length);
                Assert.Equal(36, applied.Sum(item => item.Amount));
                _ = Assert.Single(applied, item => item.Amount == 0);
            }
        }
        finally { await app.StopAsync(); }
        Assert.All(effects.Scopes.Values, Assert.True);
    }
}
