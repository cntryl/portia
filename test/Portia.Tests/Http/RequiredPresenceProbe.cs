using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

sealed record RequiredPresenceRequest(
    [property: JsonRequired] string? Marker = null,
    [property: Required] string? Name = "fallback",
    [property: Required] int Count = 0) : IRequest<string>, ICallable;

sealed class RequiredPresenceHandler : IRequestHandler<RequiredPresenceRequest, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<RequiredPresenceRequest> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(context.Request.Name!));
}
