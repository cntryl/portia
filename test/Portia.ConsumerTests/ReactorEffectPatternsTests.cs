using System.Security.Claims;

namespace Cntryl.Portia.Consumer;

public sealed class ReactorEffectPatternsTests
{
    [Fact]
    public async Task CommandReactionPreservesCheckpointGivenCommandFailure()
    {
        var aggregateId = Uuid.CreateVersion4();
        var store = await StoreWithDeposit(aggregateId);
        var checkpoints = new InMemoryProjectionCheckpointStore();
        var bus = new OutcomeBus(Result.Failure(new RequestError(RequestErrorKind.Conflict, "closed")));
        var reactor = new CommandReaction(checkpoints, bus, aggregateId);

        _ = await Assert.ThrowsAsync<ReactionCommandFailedException>(() =>
            new ReactorRunner(store).RunAsync(reactor, ProjectionCheckpoint.Start).AsTask());

        Assert.Equal(ProjectionCheckpoint.Start,
            await checkpoints.LoadAsync(new CheckpointIdentity(reactor.Name, reactor.Pattern)));
        bus.Outcome = Result.Success;
        Assert.Equal(1UL, (await new ReactorRunner(store).RunAsync(reactor, ProjectionCheckpoint.Start)).NextOffset);
    }

    [Fact]
    public async Task DirectEffectReactionRunsWithoutRequestBus()
    {
        var aggregateId = Uuid.CreateVersion4();
        var effects = new RecordingEffects();
        var reactor = new DirectEffectReaction(new InMemoryProjectionCheckpointStore(), effects, aggregateId);

        _ = await new ReactorRunner(await StoreWithDeposit(aggregateId))
            .RunAsync(reactor, ProjectionCheckpoint.Start);

        Assert.Equal(1, effects.Receipts);
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

        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            new(actor, metadata: metadata);

        public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Outcome);

        public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    sealed class RecordingEffects
    {
        public int Receipts { get; private set; }
        public ValueTask SendReceiptAsync(Deposited ev, IReactorContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(ev, context.Source.Ev))
                throw new InvalidOperationException("The direct effect lost its triggering context.");
            Receipts++;
            return ValueTask.CompletedTask;
        }
    }

    sealed record SendReceipt(Uuid AccountId) : IRequest;

    sealed class CommandReaction(IProjectionCheckpointStore checkpoints, IRequestBus bus, Uuid aggregateId)
        : BaseReactor(checkpoints, EventStreamPattern.ForPattern("testing", "reactions", aggregateId.ToString()), "command-reaction")
    {
        protected override ValueTask ReactToEventAsync(
            DomainEventRecord record, IExecutionContext context, CancellationToken ct) =>
            bus.SendReactionAsync(
                new SendReceipt(record.Ev.Metadata.AggregateId), (IReactorContext)context, ct);
    }

    sealed class DirectEffectReaction(IProjectionCheckpointStore checkpoints, RecordingEffects effects, Uuid aggregateId)
        : BaseReactor(checkpoints, EventStreamPattern.ForPattern("testing", "reactions", aggregateId.ToString()), "direct-effect-reaction")
    {
        protected override ValueTask ReactToEventAsync(
            DomainEventRecord record, IExecutionContext context, CancellationToken ct) =>
            effects.SendReceiptAsync((Deposited)record.Ev, (IReactorContext)context, ct);
    }
}
