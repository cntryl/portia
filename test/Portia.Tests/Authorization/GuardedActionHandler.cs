namespace Cntryl.Portia;

sealed class GuardedActionHandler : IRequestHandler<GuardedAction>
{
    public bool WasInvoked { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<GuardedAction> context, CancellationToken ct)
    {
        WasInvoked = true;
        return ValueTask.FromResult(Result.Success);
    }
}
