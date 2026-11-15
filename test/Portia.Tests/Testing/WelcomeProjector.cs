namespace Cntryl.Portia;

sealed partial class WelcomeProjector(List<Uuid> users, IProjectionStore store, EventStreamPattern? pattern = null)
    : Projector(store, pattern ?? EventStreamPattern.ForPattern("test", "users")),
        IProjectorHandler<UserCreated>
{
    public List<string> StreamResources { get; } = [];

    public ValueTask<Result> HandleAsync(IProjectorContext<UserCreated> context, CancellationToken ct)
    {
        StreamResources.Add(context.Source.Stream.Resource);
        users.Add(context.Trigger.UserId);
        return ValueTask.FromResult(Result.Success);
    }
}
