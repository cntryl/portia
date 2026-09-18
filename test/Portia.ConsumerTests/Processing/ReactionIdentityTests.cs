using System.Security.Claims;

namespace Cntryl.Portia.Consumer;

public sealed class ReactionIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TriggerActorDoesNotAuthorizeReactionAndSystemIdentityIsConfigurable(bool userPrincipal)
    {
        var store = new InMemoryEventStore();
        var id = Uuid.CreateVersion4();
        var source = new Deposited(1);
        source.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow)
        {
            Actor = new ActorAttribution("alice", "users"),
            ExecutionId = Uuid.CreateVersion4()
        });
        await store.AppendAsync(new EventStreamAddress("identity", "source", id.ToString()), 0, [source]);
        var actor = userPrincipal
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "user"))
            : RequestActor.CreateSystem("billing", "services");
        var reactor = new RecordingReactor();
        var clock = new Clock();
        var runner = new ReactorRunner(store, new PrincipalProvider(actor), clock);
        if (userPrincipal)
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await runner.RunAsync(reactor, ProjectionCheckpoint.Start));
            Assert.Null(reactor.Context);
            return;
        }

        _ = await runner.RunAsync(reactor, ProjectionCheckpoint.Start);
        var context = Assert.IsAssignableFrom<IExecutionContext>(reactor.Context);
        Assert.Equal("billing", context.Actor.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal(source.Metadata.EventId, context.CauseId);
        Assert.Equal(source.Metadata.EventId, context.CorrelationId);
        Assert.Equal(clock.GetUtcNow(), context.StartedAt);
        Assert.NotEqual(source.Metadata.ExecutionId, context.ExecutionId);
    }

    sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    }

    sealed class PrincipalProvider(ClaimsPrincipal actor) : IReactorPrincipalProvider
    {
        public ClaimsPrincipal GetPrincipal(Reactor reactor) => actor;
    }

    sealed class RecordingReactor() : Reactor(new InMemoryProjectionCheckpointStore(),
        EventStreamPattern.ForPattern("identity", "source"), "billing")
    {
        public IExecutionContext? Context { get; private set; }

        protected override ValueTask ReactToEventAsync(DomainEventRecord record, IExecutionContext context,
            CancellationToken ct)
        {
            Context = context;
            return ValueTask.CompletedTask;
        }
    }
}
