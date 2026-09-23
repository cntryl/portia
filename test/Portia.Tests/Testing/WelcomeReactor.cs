namespace Cntryl.Portia;

sealed partial class WelcomeReactor(IRequestBus bus, EventStreamPattern? pattern = null)
    : Reactor(new InMemoryProjectionCheckpointStore(), pattern ?? EventStreamPattern.ForPattern("test", "users")),
        IReactorHandler<UserCreated>
{
    public Uuid? LastEventId { get; private set; }

    public ValueTask HandleAsync(IReactorContext<UserCreated> context, CancellationToken ct)
    {
        LastEventId = context.Trigger.Metadata.EventId;
        return bus.SendReactionAsync(new SendWelcomeEmail(context.Trigger.UserId), context, ct);
    }
}
