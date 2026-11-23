using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

sealed record RequiredPresenceRequest(
    [property: JsonRequired] string? Marker = null,
    [property: Required] string? Name = "fallback",
    [property: Required] int Count = 0) : IRequest<string>, ICallable;

sealed record RequiredHeaderPresenceRequest([property: Required] string? Region = null)
    : IRequest<string>, ICallable;

sealed class RequiredHeaderPresenceHandler : IRequestHandler<RequiredHeaderPresenceRequest, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<RequiredHeaderPresenceRequest> context,
        CancellationToken ct
    ) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Region!));
}

sealed class RequiredPresenceHandler : IRequestHandler<RequiredPresenceRequest, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<RequiredPresenceRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Name!));
}
