using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
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
        var parent =
            new RequestContext<BrokerExecutionContextTests.Command>(new BrokerExecutionContextTests.Command(1),
                RequestActor.System);
        var routeValues = new RequestRouteValues(Resource: "actual");
        var wire = new Wire();
        IRequestNotificationConsumer consumer;
        if (scheduled)
        {
            _ = await new FitzRequestScheduler(wire, serializer).ScheduleAsync(
                new BrokerExecutionContextTests.Command(2),
                new RequestScheduleSpec("0 0 * * *"), routeValues, RequestActor.CreateSystem("scheduler"), parent);
            var stored = Encoding.UTF8.GetString(wire.Body.Span);
            Assert.DoesNotContain("actor_token", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("credential", stored, StringComparison.Ordinal);
            Assert.Contains("scheduler", stored, StringComparison.Ordinal);
            consumer = new FitzScheduledRequestConsumer(wire, serializer, "schedule://context/work/*/execute");
        }
        else
        {
            await new FitzNoticeRequestSender(wire, serializer).PublishAsync(new BrokerExecutionContextTests.Command(2),
                routeValues, "credential", parent);
            consumer = new FitzNoticeRequestConsumer(wire, serializer, "notice://context/work/*");
        }

        var noticeTemplate = scheduled ? null : serializer.DeserializeEnvelope(wire.Body);
        var handler = new BrokerExecutionContextTests.Handler();
        await using var provider = BrokerExecutionContextTests.Services(handler).BuildServiceProvider();
        await new RequestNotificationRunner(consumer,
                new DependencyInjectionRequestDeliveryScopeFactory(provider.GetRequiredService<IServiceScopeFactory>()))
            .RunAsync();
        Assert.Equal(2, handler.Contexts.Count);
        Assert.NotEqual(handler.Contexts[0].ExecutionId, handler.Contexts[1].ExecutionId);
        Assert.All(handler.Contexts, context =>
        {
            Assert.Equal(parent.CorrelationId, context.CorrelationId);
            Assert.Equal(scheduled ? handler.Contexts[0].CausationId : parent.CauseId, context.CausationId);
            Assert.Equal(scheduled ? new ScheduleInvocation(wire.Route) : new NoticeInvocation(wire.Route),
                context.Invocation);
        });
        if (scheduled)
        {
            Assert.NotEqual(parent.CauseId, handler.Contexts[0].CausationId);
            Assert.NotEqual(handler.Contexts[0].RequestId, handler.Contexts[1].RequestId);
            Assert.All(handler.Contexts, context => Assert.True(RequestActor.IsSystem(context.Actor)));
        }
        else
        {
            Assert.All(handler.Contexts,
                context => Assert.Equal(noticeTemplate!.Metadata.RequestId, context.RequestId));
        }
    }

    [Fact]
    public async Task SchedulerRejectsUserIdentityBeforePersistingAnything()
    {
        var wire = new Wire();
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "accounts")], "jwt"));

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer())
                .ScheduleAsync(new BrokerExecutionContextTests.Command(2), new RequestScheduleSpec("0 0 * * *"),
                    new RequestRouteValues(Resource: "actual"), user).AsTask());

        Assert.True(wire.Body.IsEmpty);
    }

    [Fact]
    public async Task FiredSchedulePreservesStableWireName()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var wire = new Wire();
        _ = await new FitzRequestScheduler(wire, serializer).ScheduleAsync(
            new BrokerExecutionContextTests.Command(2),
            new RequestScheduleSpec("0 0 * * *"),
            new RequestRouteValues(Resource: "actual"),
            RequestActor.CreateSystem("scheduler"));
        var consumer = new FitzScheduledRequestConsumer(
            wire, serializer, "schedule://context/work/*/execute");
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("consumer.context.command", enumerator.Current.Name);
    }

    [Fact]
    public async Task LegacyBearerTokenScheduleRequiresDrainAndRecreation()
    {
        var serializer = ConsumerJson.CreateSerializer();
        var wire = new Wire();
        await wire.PublishAsync("schedule://context/work/actual/execute",
            serializer.Serialize(new BrokerExecutionContextTests.Command(2), "legacy-bearer-token",
                RequestMetadata.Create(), null));
        var consumer = new FitzScheduledRequestConsumer(wire, serializer, "schedule://context/work/*/execute");
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        var exception =
            await Assert.ThrowsAsync<LegacyScheduledRequestException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Contains("Cancel and recreate", exception.Message, StringComparison.Ordinal);
        Assert.Contains(wire.Route, exception.Message, StringComparison.Ordinal);
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

        Task<NoticeSubscription> INoticeClient.SubscribeAsync(string selector, CancellationToken ct)
            => Task.FromResult(new NoticeSubscription(selector, Repeat(new NoticeMessage(Route, Body), ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public async Task<string?> CreateAsync(string route, string cron, ScheduleDeliveryMode mode,
            ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            await PublishAsync(route, payload, ct);
            return "schedule";
        }

        Task<ScheduleSubscription> IScheduleClient.SubscribeAsync(string selector, CancellationToken ct)
            => Task.FromResult(new ScheduleSubscription(selector, Repeat(new ScheduleNotification(Route, Body), ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task CancelAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ScheduleListPage> ListAsync(ulong? offset, ulong? limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScheduleEntry>>
            ListBySelectorAsync(string selector, CancellationToken ct = default) => throw new NotSupportedException();

        static async IAsyncEnumerable<T> Repeat<T>(T value, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return value;
            yield return value;
        }
    }
}
