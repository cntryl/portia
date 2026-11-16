using System.Text.Json.Serialization;

namespace Cntryl.Portia;

sealed record OpenApiDescribedNode(
    [property: JsonPropertyName("wire-name")] string Name,
    OpenApiDescribedNode? Next = null);

[Discriminator("test.openapi.descriptions")]
[RequestRoute("test", "openapi", "descriptions", "echo")]
sealed record OpenApiDescribedRequest(int Limit, OpenApiDescribedNode Payload) : IRequest<OpenApiDescribedNode>, ICallable;

sealed class OpenApiDescribedHandler : IRequestHandler<OpenApiDescribedRequest, OpenApiDescribedNode>
{
    public ValueTask<Result<OpenApiDescribedNode>> HandleAsync(IRequestContext<OpenApiDescribedRequest> context,
        CancellationToken ct) => ValueTask.FromResult(Result<OpenApiDescribedNode>.Success(context.Request.Payload));
}

[Discriminator("test.openapi.descriptions.other")]
[RequestRoute("test", "openapi", "descriptions", "other")]
sealed record OpenApiOtherDescribedRequest(int Limit, OpenApiDescribedNode Payload) : IRequest<OpenApiDescribedNode>, ICallable;

sealed class OpenApiOtherDescribedHandler : IRequestHandler<OpenApiOtherDescribedRequest, OpenApiDescribedNode>
{
    public ValueTask<Result<OpenApiDescribedNode>> HandleAsync(IRequestContext<OpenApiOtherDescribedRequest> context,
        CancellationToken ct) => ValueTask.FromResult(Result<OpenApiDescribedNode>.Success(context.Request.Payload));
}
