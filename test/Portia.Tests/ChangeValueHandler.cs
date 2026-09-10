namespace Cntryl.Portia;

sealed class ChangeValueHandler : IRequestHandler<ChangeValue>
{
    public int? LastValue { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<ChangeValue> context, CancellationToken ct)
    {
        LastValue = context.Request.Value;
        return ValueTask.FromResult(Result.Success);
    }
}
