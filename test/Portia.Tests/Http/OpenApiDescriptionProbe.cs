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


[System.ComponentModel.Description("Attributed node.")]
sealed record OpenApiAnnotatedNode(
    [property: System.ComponentModel.Description("Attributed name."), JsonPropertyName("wire-name")] string Name,
    [property: System.ComponentModel.DataAnnotations.Display(Description = "Attributed next.")] OpenApiAnnotatedNode? Next = null);

[System.ComponentModel.Description("Attributed request.")]
[Discriminator("test.openapi.annotated")]
[RequestRoute("test", "openapi", "annotated", "echo")]
sealed record OpenApiAnnotatedRequest([property: System.ComponentModel.Description("Attributed limit.")] int Limit,
    OpenApiAnnotatedNode Payload) : IRequest<OpenApiAnnotatedNode>, ICallable;

sealed class OpenApiAnnotatedHandler : IRequestHandler<OpenApiAnnotatedRequest, OpenApiAnnotatedNode>
{
    public ValueTask<Result<OpenApiAnnotatedNode>> HandleAsync(IRequestContext<OpenApiAnnotatedRequest> context,
        CancellationToken ct) => ValueTask.FromResult(Result<OpenApiAnnotatedNode>.Success(context.Request.Payload));
}
