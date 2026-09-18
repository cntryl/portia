namespace Cntryl.Portia;

sealed class ChangeValuePipelineBehavior : IRequestPipelineBehavior<ChangeValue>
{
    public ValueTask<Result> HandleAsync(IRequestContext<ChangeValue> context, RequestPipelineNext continuation,
        CancellationToken ct)
        => continuation(ct);
}
