using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class DeliveryScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryDeliveryOwnsScopeThroughNestedDispatchFailureAndCancellation(bool notification)
    {
        var requests = new[]
        {
            new ScopeRequest(Uuid.CreateVersion4()), new ScopeRequest(Uuid.CreateVersion4(), 1),
            new ScopeRequest(Uuid.CreateVersion4()), new ScopeRequest(Uuid.CreateVersion4(), 2)
        };
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, ScopeValidator>();
        if (notification)
        {
            _ = services.AddSingleton<IRequestNotificationConsumer>(new Notifications(requests));
            _ = services.AddPortiaRequestNotificationRunner();
        }
        else
        {
            _ = services.AddSingleton<IRequestQueueConsumer>(new Queue(requests));
            _ = services.AddPortiaQueueRunner();
        }

        await using var provider = ConsumerHost.Build(services);
        var worker = Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>());
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        try
        {
            await worker.StartAsync(default);
            await effects.WaitForAsync("nested", 4);
        }
        finally
        {
            await worker.StopAsync(default);
            (worker as IDisposable)?.Dispose();
        }

        var deliveries = effects.Items.Where(item => item.Component == "delivery").ToArray();
        Assert.Equal(4, deliveries.Select(item => item.ScopeId).Distinct().Count());
        foreach (var delivery in deliveries)
        {
            Assert.Equal(delivery.ScopeId, Assert.Single(effects.Items,
                item => item.Component == "nested" && item.AggregateId == delivery.AggregateId).ScopeId);
            Assert.Contains(effects.Items, item => item.Component == "validator" && item.ScopeId == delivery.ScopeId);
        }

        Assert.All(effects.Scopes.Values, Assert.True);
    }

    [Fact]
    public async Task RpcServerResolvesFreshApplicationScopeForEveryCall()
    {
        var services = ConsumerHost.CreateServices();
        _ = services.AddAccounts();
        _ = services.AddScoped<IRequestActorValidator, ScopeValidator>();
        _ = services.AddSingleton<IRpcClient, InMemoryRpcClient>();
        var serializer = ConsumerJson.CreateSerializer();
        _ = services.AddSingleton<IRequestDeserializer>(serializer);
        _ = services.AddSingleton<IRequestOutcomeSerializer>(serializer);
        _ = services.AddSingleton<FitzRpcRequestServer>();
        await using var provider = ConsumerHost.Build(services);
        var server = provider.GetRequiredService<FitzRpcRequestServer>();
        await using var registration = await server.RegisterAsync<ScopeRequest>();
        var sender = new FitzRemoteRequestSender(provider.GetRequiredService<IRpcClient>(), serializer, serializer);
        for (var i = 0; i < 2; i++)
        {
            Assert.True(
                (await sender.SendAsync(new ScopeRequest(Uuid.CreateVersion4()), new RequestRouteValues(), null))
                .IsSuccess);
        }

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sender.SendAsync(new ScopeRequest(Uuid.CreateVersion4(), 1), new RequestRouteValues(), null));
        var effects = provider.GetRequiredService<ConsumerHost.Effects>();
        Assert.Equal(3,
            effects.Items.Where(item => item.Component == "delivery").Select(item => item.ScopeId).Distinct().Count());
        Assert.All(effects.Scopes.Values, Assert.True);
    }

    public sealed class ScopeValidator(IConsumerScope scope, IConsumerEffects effects) : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
        {
            effects.Record("validator", default, 0, scope.Id);
            return ValueTask.FromResult(Result<ClaimsPrincipal>.Success(new ClaimsPrincipal()));
        }
    }

    sealed class Queue(ScopeRequest[] requests) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                yield return new Queued(request);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    sealed class Queued(IRequest request) : IQueuedRequest
    {
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/item", Attempt);
        public IRequest Request => request;
        public string? ActorToken => null;
        public uint Attempt => 1;
        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class Notifications(ScopeRequest[] requests) : IRequestNotificationConsumer
    {
        public async IAsyncEnumerable<RequestNotification> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                yield return new RequestNotification(request, null, RequestMetadata.Create(),
                    new NoticeInvocation("notice://test/work/item"));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
