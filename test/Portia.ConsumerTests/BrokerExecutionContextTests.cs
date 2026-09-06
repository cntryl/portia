using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class BrokerExecutionContextTests
{
    [Fact]
    public async Task RpcChildKeepsCausalityAndStampsReceiverRouteAndActor()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        var handler = new Handler();
        var services = Services(handler);
        await using var provider = services.BuildServiceProvider();
        var server = new FitzRpcRequestServer(client.Rpc, provider.GetRequiredService<IServiceScopeFactory>());
        await using var registration = await server.RegisterAsync<Command>();
        var serializer = new JsonRequestSerializer();
        var sender = new FitzRemoteRequestSender(client.Rpc, serializer, serializer);
        var parent = new RequestContext<Command>(new Command(1), RequestActor.CreateSystem("sender"));
        var resource = Uuid.CreateVersion4().ToString();
        Assert.True((await sender.SendAsync(new Command(2), new RequestRouteValues(Resource: resource), "credential", parent)).IsSuccess);
        var execution = Assert.Single(handler.Contexts);
        Assert.Equal(parent.CorrelationId, execution.CorrelationId);
        Assert.Equal(parent.CauseId, execution.CausationId);
        Assert.NotEqual(parent.ExecutionId, execution.ExecutionId);
        Assert.NotEqual(parent.CauseId, execution.RequestId);
        Assert.Equal(new RpcInvocation($"rpc://context/work/{resource}/execute"), execution.Invocation);
        Assert.Equal("receiver", execution.Actor.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    }

    [Fact]
    public async Task QueueBrokerRedeliveryPreservesEnvelopeAndReportsTransportAttempt()
    {
        await using var client = await ConsumerBroker.ConnectAsync();
        var serializer = new JsonRequestSerializer();
        var parent = new RequestContext<Command>(new Command(1), RequestActor.System);
        var metadata = RequestMetadata.FromParent(parent);
        var resource = Uuid.CreateVersion4().ToString();
        var route = $"queue://context/work/{resource}";
        var publisher = new FitzRequestQueuePublisher(client.Queue, serializer);
        await publisher.EnqueueAsync(new Command(2), new RequestRouteValues(Resource: resource), "credential", metadata);
        var consumer = new FitzRequestQueueConsumer(client.Queue, serializer, route, visibilityTimeoutSeconds: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var deliveries = consumer.ReadAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await deliveries.MoveNextAsync());
        var first = deliveries.Current;
        Assert.Equal(metadata, first.Metadata);
        var firstExecution = new RequestDispatchContext(RequestActor.System, first.Invocation, first.Metadata);
        await first.AbandonAsync(deadline.Token);
        Assert.True(await deliveries.MoveNextAsync());
        var second = deliveries.Current;
        Assert.Equal(metadata, second.Metadata);
        Assert.True(second.Attempt > 0);
        Assert.Equal(new QueueInvocation(route, second.Attempt), second.Invocation);
        var secondExecution = new RequestDispatchContext(RequestActor.System, second.Invocation, second.Metadata);
        Assert.Equal(firstExecution.RequestId, secondExecution.RequestId);
        Assert.NotEqual(firstExecution.ExecutionId, secondExecution.ExecutionId);
        await second.CompleteAsync(deadline.Token);
    }

    internal static ServiceCollection Services(Handler handler)
    {
        var services = new ServiceCollection();
        _ = services.AddPortiaModule<ContractsModule>();
        _ = services.AddSingleton(handler);
        _ = services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<Command, Handler>());
        _ = services.AddSingleton<IRequestDeserializer, JsonRequestSerializer>();
        _ = services.AddSingleton<IRequestOutcomeSerializer, JsonRequestSerializer>();
        _ = services.AddSingleton<IRequestActorValidator, Validator>();
        return services;
    }

    [RequestRoute("context", "work", "*", "execute")]
    public sealed record Command(int Amount) : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;

    public sealed class Handler : IRequestHandler<Command>
    {
        public List<IRequestContext<Command>> Contexts { get; } = [];
        public ValueTask<Result> HandleAsync(IRequestContext<Command> context, CancellationToken ct)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(Result.Success);
        }
    }

    sealed class Validator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
            => ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.CreateSystem("receiver")));
    }
}
