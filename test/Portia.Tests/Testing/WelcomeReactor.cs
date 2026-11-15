namespace Cntryl.Portia;

sealed partial class WelcomeReactor(IRequestBus bus, EventStreamPattern? pattern = null)
    : Reactor(new InMemoryProjectionCheckpointStore(), pattern ?? EventStreamPattern.ForPattern("test", "users")),
        IReactorHandler<UserCreated>
{
    public Uuid? LastEventId { get; private set; }
    public string? LastStreamRealm { get; private set; }
    public List<string> StreamResources { get; } = [];

    public ValueTask<Result> HandleAsync(IReactorContext<UserCreated> context, CancellationToken ct)
    {
        LastEventId = context.Trigger.Metadata.EventId;
        LastStreamRealm = context.Source.Stream.Realm;
        StreamResources.Add(context.Source.Stream.Resource);
        return bus.SendReactionAsync(new SendWelcomeEmail(context.Trigger.UserId), context, ct);
    }
}
