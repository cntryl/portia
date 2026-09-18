namespace Cntryl.Portia;

sealed class HttpSearchWidgetsHandler : IRequestHandler<HttpSearchWidgets, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpSearchWidgets> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success($"{context.Request.Filter.Name}:{context.Request.Filter.Limit}"));
}
