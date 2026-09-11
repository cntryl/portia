using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cntryl.Fitz.Abstractions.Domains.Notice;
using Cntryl.Fitz.Abstractions.Domains.Schedule;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class NotificationExecutionTests
{
    [Fact]
    public async Task EnsuredScheduleDefinitionsAreStableAndContainNoTraceContext()
    {
        static async Task<(string Route, byte[] Body)> EnsureAsync()
        {
            var wire = new Wire();
            using var activity = new System.Diagnostics.Activity("host-start").Start();
            _ = await new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer()).EnsureAsync(
                new BrokerExecutionContextTests.Command(2), new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues(Resource: "actual"), RequestActor.CreateSystem("scheduler"));
            return (wire.Route, wire.Body.ToArray());
        }

        var first = await EnsureAsync();
        var second = await EnsureAsync();

        Assert.Equal("schedule://context/work/actual/execute", first.Route);
        Assert.Equal(first.Body, second.Body);
        Assert.DoesNotContain("trace", Encoding.UTF8.GetString(first.Body), StringComparison.OrdinalIgnoreCase);
        var expected = Uuid.CreateVersion5(Uuid.UrlNamespace, "portia:schedule:" + first.Route).ToString();
        using var document = JsonDocument.Parse(first.Body);
        var requestEnvelope = document.RootElement.GetProperty("request_envelope").GetBytesFromBase64();
        var envelope = ConsumerJson.CreateSerializer().DeserializeEnvelope(requestEnvelope);
        Assert.Equal(expected, envelope.Metadata.RequestId.ToString());
        Assert.Equal(envelope.Metadata.RequestId, envelope.Metadata.CorrelationId);
        Assert.Null(envelope.TraceContext);
    }

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

    [Theory]
    [InlineData(2, "scheduler", "portia")]
    [InlineData(1, "", "portia")]
    [InlineData(1, "   ", "portia")]
    [InlineData(1, "scheduler", "")]
    [InlineData(1, "scheduler", "   ")]
    public async Task FiredScheduleWithoutACompleteSystemIdentityIsRejected(int version, string subject, string issuer)
    {
        var serializer = ConsumerJson.CreateSerializer();
        var wire = new Wire();
        var request = serializer.Serialize(new BrokerExecutionContextTests.Command(2), null,
            RequestMetadata.Create(), null);
        await wire.PublishAsync("schedule://context/work/actual/execute", JsonSerializer.SerializeToUtf8Bytes(
            new { version, system_subject = subject, system_issuer = issuer, request_envelope = request.ToArray() }));
        var consumer = new FitzScheduledRequestConsumer(wire, serializer, "schedule://context/work/*/execute");
        await using var enumerator = consumer.ReadAsync().GetAsyncEnumerator();

        // The entry is dropped rather than dispatched, and dropping it does not end the
        // subscription; the recorded runner fault carries why (see FitzNotificationFaultTests).
        Assert.False(await enumerator.MoveNextAsync());
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

        // A legacy entry is never dispatched, and one of them no longer stops every other
        // schedule on the route; FitzNotificationFaultTests covers the reported diagnostic.
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Theory]
    [InlineData(RequestScheduleDeliveryMode.One, ScheduleDeliveryMode.Single)]
    [InlineData(RequestScheduleDeliveryMode.Broadcast, ScheduleDeliveryMode.Broadcast)]
    public async Task DeliveryModeIsCarriedToTheBrokerUntranslated(RequestScheduleDeliveryMode requested,
        ScheduleDeliveryMode expected)
    {
        var wire = new Wire();

        var id = await new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer()).ScheduleAsync(
            new BrokerExecutionContextTests.Command(2),
            new RequestScheduleSpec("0 0 * * *", requested),
            new RequestRouteValues(Resource: "actual"),
            RequestActor.CreateSystem("scheduler"));

        Assert.Equal(expected, wire.Mode);
        Assert.Equal("schedule", id);
    }

    [Fact]
    public async Task UndefinedDeliveryModeIsRejectedBeforePersistingAnything()
    {
        var wire = new Wire();

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer()).ScheduleAsync(
                new BrokerExecutionContextTests.Command(2),
                new RequestScheduleSpec("0 0 * * *", (RequestScheduleDeliveryMode)42),
                new RequestRouteValues(Resource: "actual"),
                RequestActor.CreateSystem("scheduler")).AsTask());

        Assert.True(wire.Body.IsEmpty);
    }

    [Fact]
    public async Task ScheduleWithoutABrokerIdentityFailsInsteadOfReturningNothingToCancelWith()
    {
        var wire = new Wire { ReturnNoIdentity = true };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer()).ScheduleAsync(
                new BrokerExecutionContextTests.Command(2),
                new RequestScheduleSpec("0 0 * * *"),
                new RequestRouteValues(Resource: "actual"),
                RequestActor.CreateSystem("scheduler")).AsTask());

        Assert.Contains("did not return an identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingAScheduleForwardsTheBrokerIdentityItWasCreatedWith()
    {
        var wire = new Wire();
        var scheduler = new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer());
        var id = await scheduler.ScheduleAsync(
            new BrokerExecutionContextTests.Command(2),
            new RequestScheduleSpec("0 0 * * *"),
            new RequestRouteValues(Resource: "actual"),
            RequestActor.CreateSystem("scheduler"));

        await scheduler.CancelAsync(id);

        Assert.Equal(id, Assert.Single(wire.Cancellations));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CancellingWithoutAScheduleIdentityIsRejectedBeforeReachingTheBroker(string? scheduleId)
    {
        var wire = new Wire();

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            new FitzRequestScheduler(wire, ConsumerJson.CreateSerializer()).CancelAsync(scheduleId!).AsTask());

        Assert.Empty(wire.Cancellations);
    }

    sealed class Wire : INoticeClient, IScheduleClient
    {
        public ReadOnlyMemory<byte> Body { get; private set; }
        public string Route { get; private set; } = "";
        public ScheduleDeliveryMode? Mode { get; private set; }
        public List<string> Cancellations { get; } = [];
        public bool ReturnNoIdentity { get; init; }

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
            Mode = mode;
            await PublishAsync(route, payload, ct);
            return ReturnNoIdentity ? null : "schedule";
        }

        Task<ScheduleSubscription> IScheduleClient.SubscribeAsync(string selector, CancellationToken ct)
            => Task.FromResult(new ScheduleSubscription(selector, Repeat(new ScheduleNotification(Route, Body), ct),
                _ => ValueTask.CompletedTask, Task.CompletedTask));

        public Task CancelAsync(string id, CancellationToken ct = default)
        {
            Cancellations.Add(id);
            return Task.CompletedTask;
        }

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
