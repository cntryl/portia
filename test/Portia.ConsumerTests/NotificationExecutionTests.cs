using System.Runtime.CompilerServices;
using Cntryl.Fitz.Abstractions.Domains.Notice;
using Cntryl.Fitz.Abstractions.Domains.Schedule;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class NotificationExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationDispatchUsesConcreteRouteAndScheduleFiringsHaveIndependentIdentities(bool scheduled)
    {
        var serializer = ConsumerJson.CreateSerializer();
        var parent = new RequestContext<BrokerExecutionContextTests.Command>(new(1), RequestActor.System);
        var routeValues = new RequestRouteValues(Resource: "actual");
        var wire = new Wire();
        IRequestNotificationConsumer consumer;
        if (scheduled)
        {
            _ = await new FitzRequestScheduler(wire, serializer).ScheduleAsync(new BrokerExecutionContextTests.Command(2),
                new RequestScheduleSpec("0 0 * * *"), routeValues, "credential", parent);
            consumer = new FitzScheduledRequestConsumer(wire, serializer, "schedule://context/work/*/execute");
        }
        else
        {
            await new FitzNoticeRequestSender(wire, serializer).PublishAsync(new BrokerExecutionContextTests.Command(2), routeValues, "credential", parent);
            consumer = new FitzNoticeRequestConsumer(wire, serializer, "notice://context/work/*");
        }
        var template = serializer.DeserializeEnvelope(wire.Body).Metadata;
        Assert.Equal(parent.CauseId, template.CausationId);
        var handler = new BrokerExecutionContextTests.Handler();
        await using var provider = BrokerExecutionContextTests.Services(handler).BuildServiceProvider();
        await new RequestNotificationRunner(consumer, provider.GetRequiredService<IServiceScopeFactory>()).RunAsync();
        Assert.Equal(2, handler.Contexts.Count);
        Assert.NotEqual(handler.Contexts[0].ExecutionId, handler.Contexts[1].ExecutionId);
        Assert.All(handler.Contexts, context =>
        {
            Assert.Equal(parent.CorrelationId, context.CorrelationId);
            Assert.Equal(scheduled ? template.RequestId : parent.CauseId, context.CausationId);
            Assert.Equal(scheduled ? new ScheduleInvocation(wire.Route) : new NoticeInvocation(wire.Route), context.Invocation);
        });
        if (scheduled)
        {
            Assert.NotEqual(template.RequestId, handler.Contexts[0].RequestId);
            Assert.NotEqual(handler.Contexts[0].RequestId, handler.Contexts[1].RequestId);
        }
        else
        {
            Assert.All(handler.Contexts, context => Assert.Equal(template.RequestId, context.RequestId));
        }
    }

    sealed class Wire : INoticeClient, IScheduleClient
    {
        public ReadOnlyMemory<byte> Body { get; private set; }
        public string Route { get; private set; } = "";
        public Task PublishAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            Route = route;
            Body = body;
            return Task.CompletedTask;
        }
        public async Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            await PublishAsync(route, payload, ct);
            return "schedule";
        }
        Task<NoticeSubscription> INoticeClient.SubscribeAsync(string selector, CancellationToken ct)
            => Task.FromResult(new NoticeSubscription(selector, Repeat(new NoticeMessage(Route, Body), ct), _ => ValueTask.CompletedTask, Task.CompletedTask));
        Task<ScheduleSubscription> IScheduleClient.SubscribeAsync(string selector, CancellationToken ct)
            => Task.FromResult(new ScheduleSubscription(selector, Repeat(new ScheduleNotification(Route, Body), ct), _ => ValueTask.CompletedTask, Task.CompletedTask));
        public Task CancelAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ScheduleEntry>> ListBySelectorAsync(string selector, CancellationToken ct = default) => throw new NotSupportedException();
        static async IAsyncEnumerable<T> Repeat<T>(T value, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return value;
            yield return value;
        }
    }
}
