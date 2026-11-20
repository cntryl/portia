using System.Security.Claims;

namespace Cntryl.Portia.Consumer;

public sealed class ReactorEffectPatternsTests
{
    [Fact]
    public async Task ShouldPreserveCheckpointGivenCommandReactionFailure()
    {
        // Arrange
        var aggregateId = Uuid.CreateVersion4();
        var store = await StoreWithDeposit(aggregateId);
        var checkpoints = new InMemoryProjectionCheckpointStore();
        var bus = new OutcomeBus(Result.Failure(new RequestError(RequestErrorKind.Conflict, "closed")));

        // Act
        var reactor = new CommandReaction(checkpoints, bus, aggregateId);

        // Assert
        _ = await Assert.ThrowsAsync<ProcessorFailureException>(() =>
            new ReactorRunner(store).RunAsync(reactor, ProjectionCheckpoint.Start).AsTask());

        Assert.Equal(ProjectionCheckpoint.Start,
            await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern)));
        bus.Outcome = Result.Success;
        Assert.Equal("1",
            (await new ReactorRunner(store).RunAsync(reactor, ProjectionCheckpoint.Start)).Cursor.ToString());
    }

    [Fact]
    public async Task ShouldRunReactionGivenDirectEffectWithoutRequestBus()
    {
        // Arrange
        var aggregateId = Uuid.CreateVersion4();
        var effects = new RecordingEffects();
        var reactor = new DirectEffectReaction(new InMemoryProjectionCheckpointStore(), effects, aggregateId);

        // Act
        _ = await new ReactorRunner(await StoreWithDeposit(aggregateId))
            .RunAsync(reactor, ProjectionCheckpoint.Start);

        // Assert
        Assert.Equal(1, effects.Receipts);
    }

    [Fact]
    public async Task ShouldResolveRecordedRefusalGivenAnAdvisorySuccessAndUnknownFirstOutcome()
    {
        // Arrange
        var orderId = Uuid.CreateVersion4();
        var trigger = DomainEventSeed.Attach(new PlacedAfterAvailabilityCheck(), orderId, 1);
        var store = new InMemoryEventStore();
        var stream = new EventStreamAddress("testing", "orders", orderId.ToString());
        await store.AppendAsync(stream, 0, [trigger]);
        var participant = new InventoryParticipant(store);
        Assert.True(participant.CanReserve);
        participant.CanReserve = false;
        var checkpoints = new InMemoryProjectionCheckpointStore();
        var reactor = new ReservationReaction(checkpoints, participant, orderId);
        var runner = new ReactorRunner(store);
        var identity = new CheckpointIdentity(reactor.Name, reactor.Pattern);

        // Act
        var firstFailure = await Record.ExceptionAsync(() =>
            runner.RunAsync(reactor, ProjectionCheckpoint.Start).AsTask());
        var unknownCheckpoint = await checkpoints.LoadAsync(identity);
        var resolvedBeforeReplay = participant.RejectedOrders.Count;
        var resolvedCheckpoint = await runner.RunAsync(reactor, unknownCheckpoint);
        _ = await runner.RunAsync(reactor, ProjectionCheckpoint.Start);

        // Assert
        Assert.IsType<ProcessorFailureException>(firstFailure);
        Assert.Equal(ProjectionCheckpoint.Start, unknownCheckpoint);
        Assert.Equal(0, resolvedBeforeReplay);
        Assert.Equal("1", resolvedCheckpoint.Cursor.ToString());
        Assert.Single(participant.RejectedOrders);
        Assert.Equal(1, participant.CommittedRefusals);
        Assert.Contains(trigger.Metadata.EventId, participant.RejectedOrders);
        Assert.Equal(3, participant.Reservations.Count);
        Assert.All(participant.Reservations, context =>
        {
            Assert.Equal(trigger.Metadata.EventId, context.RequestId);
            Assert.Equal(trigger.Metadata.EventId, context.CausationId);
            Assert.True(RequestActor.IsSystem(context.Actor));
        });
    }

    sealed record PlacedAfterAvailabilityCheck : DomainEvent;

    sealed record InventoryRefused : DomainEvent;

    sealed record ReserveInventory(Uuid RequestId) : IRequest<ReservationDecision>, IIdentifiedRequest;

    sealed record RejectOrder(Uuid RequestId) : IRequest, IIdentifiedRequest;

    enum ReservationDecision
    {
        Reserved,
        Refused
    }

    sealed class InventoryParticipant(IEventStore store) : IRequestBus
    {
        public bool CanReserve { get; set; } = true;
        public int CommittedRefusals { get; private set; }
        public HashSet<Uuid> RejectedOrders { get; } = [];
        public List<RequestDispatchContext> Reservations { get; } = [];

        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            new(actor, metadata: metadata);

        public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
            CancellationToken ct = default) => ValueTask.FromResult(Result.Success);

        public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var rejected = (RejectOrder)request;
            _ = RejectedOrders.Add(rejected.RequestId);
            return ValueTask.FromResult(Result.Success);
        }

        public async ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
            CancellationToken ct = default)
        {
            var reserve = (ReserveInventory)(object)request;
            Reservations.Add(context);
            var stream = new EventStreamAddress("testing", "inventory", reserve.RequestId.ToString());
            await foreach (var record in store.ReadAsync(stream, 0, ct).ConfigureAwait(false))
            {
                if (record.Event is InventoryRefused)
                    return Result<TOut>.Success((TOut)(object)ReservationDecision.Refused);
            }

            if (CanReserve)
                return Result<TOut>.Success((TOut)(object)ReservationDecision.Reserved);

            var refusal = DomainEventSeed.Attach(new InventoryRefused(), reserve.RequestId, 1);
            await store.AppendAsync(stream, 0, [refusal], ct).ConfigureAwait(false);
            CommittedRefusals++;
            // The participant committed a refusal, but its caller has not learned that outcome yet.
            return Result<TOut>.Failure(new RequestError(RequestErrorKind.Conflict, "Reservation outcome is unknown.",
                isTransient: true));
        }

        public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
            RequestDispatchContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class ReservationReaction(IProjectionCheckpointStore checkpoints, IRequestBus bus, Uuid orderId)
        : Reactor(checkpoints, EventStreamPattern.ForPattern("testing", "orders", orderId.ToString()),
            "reservation-reaction")
    {
        protected override async ValueTask<Result> ReactToEventAsync(
            DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        {
            var id = record.Event.Metadata.EventId;
            var outcome = await bus.SendAsync(new ReserveInventory(id), context, ct).ConfigureAwait(false);
            if (!outcome.IsSuccess)
                return outcome.Error;
            if (outcome.Value == ReservationDecision.Refused)
                return await bus.SendReactionAsync(new RejectOrder(id), (IReactorContext)context, ct)
                    .ConfigureAwait(false);

            return Result.Success;
        }
    }

    static async ValueTask<InMemoryEventStore> StoreWithDeposit(Uuid aggregateId)
    {
        var ev = new Deposited(10);
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow, Uuid.CreateVersion4()));
        var store = new InMemoryEventStore();
        await store.AppendAsync(new EventStreamAddress("testing", "reactions", aggregateId.ToString()), 0, [ev]);
        return store;
    }

    sealed class OutcomeBus(Result outcome) : IRequestBus
    {
        public Result Outcome { get; set; } = outcome;

        public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
            CancellationToken ct = default) => ValueTask.FromResult(Result.Success);

        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            new(actor, metadata: metadata);

        public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(Outcome);

        public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
            RequestDispatchContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    sealed class RecordingEffects
    {
        public int Receipts { get; private set; }

        public ValueTask SendReceiptAsync(Deposited ev, IReactorContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ev, context.Source.Event))
            {
                throw new InvalidOperationException("The direct effect lost its triggering context.");
            }

            Receipts++;
            return ValueTask.CompletedTask;
        }
    }

    sealed record SendReceipt(Uuid AccountId) : IRequest;

    sealed class CommandReaction(IProjectionCheckpointStore checkpoints, IRequestBus bus, Uuid aggregateId)
        : Reactor(checkpoints, EventStreamPattern.ForPattern("testing", "reactions", aggregateId.ToString()),
            "command-reaction")
    {
        protected override async ValueTask<Result> ReactToEventAsync(
            DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        {
            return await bus.SendReactionAsync(
                new SendReceipt(record.Event.Metadata.AggregateId), (IReactorContext)context, ct).ConfigureAwait(false);
        }
    }

    sealed class DirectEffectReaction(
        IProjectionCheckpointStore checkpoints,
        RecordingEffects effects,
        Uuid aggregateId)
        : Reactor(checkpoints, EventStreamPattern.ForPattern("testing", "reactions", aggregateId.ToString()),
            "direct-effect-reaction")
    {
        protected override async ValueTask<Result> ReactToEventAsync(
            DomainEventRecord record, IExecutionContext context, CancellationToken ct)
        {
            await effects.SendReceiptAsync((Deposited)record.Event, (IReactorContext)context, ct).ConfigureAwait(false);
            return Result.Success;
        }
    }
}
