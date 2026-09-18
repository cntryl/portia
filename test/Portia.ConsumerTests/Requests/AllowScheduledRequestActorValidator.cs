using System.Security.Claims;

namespace Cntryl.Portia.Consumer;

sealed class AllowScheduledRequestActorValidator : IScheduledRequestActorValidator
{
    public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(
        string route, string subject, string issuer, CancellationToken ct = default) =>
        ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.CreateSystem(subject, issuer)));
}
