namespace Cntryl.Portia;

sealed class UniversalActionHandler : IRequestHandler<UniversalAction>
{
    readonly List<int> _handledValues = [];

    public IReadOnlyList<int> HandledValues => _handledValues;

    public ValueTask<Result> HandleAsync(IRequestContext<UniversalAction> context, CancellationToken ct)
    {
        _handledValues.Add(context.Request.Value);
        return ValueTask.FromResult(Result.Success);
    }
}
