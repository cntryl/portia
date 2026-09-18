namespace Cntryl.Portia;

[RequestRoute("*", "http-cross-origin-tests", "replace", "put")]
[Discriminator("test.http.cross-origin.replace")]
sealed record HttpCrossOriginReplace(string Value = "replaced") : IRequest<string>, ICallable;

[RequestRoute("*", "http-cross-origin-tests", "patch", "patch")]
[Discriminator("test.http.cross-origin.patch")]
sealed record HttpCrossOriginPatch(string Value = "patched") : IRequest<string>, ICallable;

[RequestRoute("*", "http-cross-origin-tests", "remove", "delete")]
[Discriminator("test.http.cross-origin.remove")]
sealed record HttpCrossOriginRemove(string Value = "removed") : IRequest<string>, ICallable;

[RequestRoute("*", "http-cross-origin-tests", "custom", "post")]
[Discriminator("test.http.cross-origin.custom")]
sealed record HttpCrossOriginCustom(string Value = "custom") : IRequest<string>, ICallable;

sealed class HttpCrossOriginReplaceHandler : IRequestHandler<HttpCrossOriginReplace, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<HttpCrossOriginReplace> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Value));
}

sealed class HttpCrossOriginPatchHandler : IRequestHandler<HttpCrossOriginPatch, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpCrossOriginPatch> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Value));
}

sealed class HttpCrossOriginRemoveHandler : IRequestHandler<HttpCrossOriginRemove, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<HttpCrossOriginRemove> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Value));
}

sealed class HttpCrossOriginCustomHandler : IRequestHandler<HttpCrossOriginCustom, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<HttpCrossOriginCustom> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Value));
}
