namespace Cntryl.Portia;

/// <summary>Processing success consumes participant authority without turning advisory reads into guarantees.</summary>
public sealed partial class OperationalValidationAuthorityTests
{
    /// <summary>An advisory stock check passes, a competing reservation wins, and choreography records rejection idempotently.</summary>
    [Fact]
    public async Task ShouldRecordAuthoritativeRejectionAsSuccessfulProcessingAfterAdvisoryRace()
    {
        var events = new InMemoryEventStore();
        var repository = new AggregateRepository(events);
        var inventoryId = Uuid.CreateVersion4();
        var orderId = Uuid.CreateVersion4();
        var principal = new RequestDispatchContext(RequestActor.System);
        var inventory = new Inventory(inventoryId);
        inventory.AddStock();
        await repository.SaveAsync(inventory, principal);
        var advisory = await repository.HydrateAsync(new Inventory(inventoryId));
        Assert.Equal(1, advisory.Available);
        var order = new Order(orderId);
        order.Place(inventoryId);
        await repository.SaveAsync(order, principal);
        Assert.Equal("pending", order.State);

        // A different participant operation commits after the successful observation.
        var competing = await repository.HydrateAsync(new Inventory(inventoryId));
        Assert.True(competing.Reserve(Uuid.CreateVersion4()));
        await repository.SaveAsync(competing, principal);

        var journal = new MemoryOperationalStore();
        var target = new OperationalTarget("reservations", "stream://authority/orders/*", OperationalScope.Global);
        var session = new OperationalSession(journal, new OperationalOptions(), new EmptyApplicationTopology(), TimeProvider.System);
        await session.AcquireAsync(target, "reactor", new OperationalProcessingSettings(512, 4096, 3, 1, 1), ProjectionCheckpoint.Start, default);
        var reactor = new ReservationReactor(journal, repository);
        reactor.BindWorkload(new WorkloadIdentity(target.Processor), target.Processor);
        var checkpoint = await new ReactorRunner(events) { Operations = session }.RunAsync(reactor, ProjectionCheckpoint.Start);
        Assert.NotEqual(ProjectionCheckpoint.Start, checkpoint);
        Assert.Null(session.State.Block);
        Assert.All(session.State.Attempts, a => Assert.Equal("committed", a.Outcome));
        Assert.Equal("rejected", (await repository.HydrateAsync(new Order(orderId))).State);

        // Replaying the original trigger obtains the already committed participant decision.
        _ = await new ReactorRunner(events).RunAsync(new ReservationReactor(new InMemoryProjectionCheckpointStore(), repository),
            ProjectionCheckpoint.Start);
        var decisions = new List<DomainEvent>();
        await foreach (var record in events.ReadAsync(new Inventory(inventoryId).Stream, 0, default))
            decisions.Add(record.Event);
        Assert.Single(decisions.OfType<ReservationRefused>());
        Assert.Single(decisions.OfType<ReservationAccepted>());
        var outcomes = new List<DomainEvent>();
        await foreach (var record in events.ReadAsync(new Order(orderId).Stream, 0, default))
            outcomes.Add(record.Event);
        Assert.Single(outcomes.OfType<OrderRejected>());
        Assert.DoesNotContain(outcomes, outcome => outcome is OrderConfirmed);
    }

    [Discriminator("test.authority.stockadded")]
    internal sealed record StockAdded : DomainEvent;
    [Discriminator("test.authority.reservationaccepted")]
    internal sealed record ReservationAccepted(Uuid Reservation) : DomainEvent;
    [Discriminator("test.authority.reservationrefused")]
    internal sealed record ReservationRefused(Uuid Reservation) : DomainEvent;
    [Discriminator("test.authority.orderplaced")]
    internal sealed record OrderPlaced(Uuid Inventory) : DomainEvent;
    [Discriminator("test.authority.orderrejected")]
    internal sealed record OrderRejected : DomainEvent;
    [Discriminator("test.authority.orderconfirmed")]
    internal sealed record OrderConfirmed : DomainEvent;

    sealed class Inventory : Aggregate
    {
        readonly Dictionary<Uuid, bool> _decisions = [];
        internal Inventory(Uuid id) : base(id, new EventStreamAddress("authority", "inventory", id.ToString()))
        {
            On<StockAdded>(_ => Available++);
            On<ReservationAccepted>(e => { Available--; _decisions.Add(e.Reservation, true); });
            On<ReservationRefused>(e => _decisions.Add(e.Reservation, false));
        }
        internal int Available { get; private set; }
        internal void AddStock() => RaiseEvent(new StockAdded());
        internal bool Reserve(Uuid reservation)
        {
            if (_decisions.TryGetValue(reservation, out var existing))
                return existing;
            if (Available == 0)
            { RaiseEvent(new ReservationRefused(reservation)); return false; }
            RaiseEvent(new ReservationAccepted(reservation));
            return true;
        }
    }

    sealed class Order : Aggregate
    {
        internal Order(Uuid id) : base(id, new EventStreamAddress("authority", "orders", id.ToString()))
        {
            On<OrderPlaced>(_ => State = "pending");
            On<OrderRejected>(_ => State = "rejected");
            On<OrderConfirmed>(_ => State = "confirmed");
        }
        internal string State { get; private set; } = "new";
        internal void Place(Uuid inventory) => RaiseEvent(new OrderPlaced(inventory));
        internal void Complete(bool accepted)
        {
            if (State != "pending")
                return;
            if (accepted)
                RaiseEvent(new OrderConfirmed());
            else
                RaiseEvent(new OrderRejected());
        }
    }

    internal sealed partial class ReservationReactor(IProjectionCheckpointStore checkpoints, IAggregateReader reader,
        IAggregateWriter writer) : Reactor(checkpoints, EventStreamPattern.ForPattern("authority", "orders")), IReactorHandler<OrderPlaced>
    {
        internal ReservationReactor(IProjectionCheckpointStore checkpoints, AggregateRepository repository)
            : this(checkpoints, repository, repository) { }

        public async ValueTask<Result> HandleAsync(IReactorContext<OrderPlaced> context, CancellationToken ct)
        {
            var inventory = await reader.HydrateAsync(new Inventory(context.Trigger.Inventory), ct);
            // This is the participant's atomic business decision, committed before completing Order.
            var accepted = inventory.Reserve(context.Trigger.Metadata.EventId);
            await writer.SaveAsync(inventory, context, ct);
            var order = await reader.HydrateAsync(new Order(context.Trigger.Metadata.AggregateId), ct);
            order.Complete(accepted);
            await writer.SaveAsync(order, context, ct);
            return Result.Success;
        }
    }
}
