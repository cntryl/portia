namespace Cntryl.Portia;

sealed partial class WelcomeProjector(List<Uuid> users, IProjectionStore store, EventStreamPattern? pattern = null)
    : Projector(store, pattern ?? EventStreamPattern.ForPattern("test", "users")),
        IProjectorHandler<UserCreated>
{
    public ValueTask HandleAsync(UserCreated ev, IProjectorContext context, CancellationToken ct)
    {
        users.Add(ev.UserId);
        return ValueTask.CompletedTask;
    }
}
