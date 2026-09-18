namespace Cntryl.Portia;

sealed class HttpOptionalBodyHandler : IRequestHandler<HttpOptionalBody, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpOptionalBody> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Value));
}
