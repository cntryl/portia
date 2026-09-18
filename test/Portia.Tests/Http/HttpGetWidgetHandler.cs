namespace Cntryl.Portia;

sealed class HttpGetWidgetHandler : IRequestHandler<HttpGetWidget, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpGetWidget> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(
            $"{context.Request.WidgetId} (archived: {context.Request.IncludeArchived})"));
}
