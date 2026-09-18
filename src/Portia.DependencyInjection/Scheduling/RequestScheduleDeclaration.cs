using System.Security.Claims;

namespace Cntryl.Portia;

sealed class RequestScheduleDeclaration<TRequest>(
    TRequest request,
    RequestScheduleSpec spec,
    RequestRouteValues routeValues,
    ClaimsPrincipal actor) : IRequestScheduleDeclaration
    where TRequest : IRequest, ISchedulable
{
    public void Validate()
    {
        if (!RequestActor.IsSystem(actor) || actor.FindFirst(ClaimTypes.NameIdentifier) is null)
        {
            throw new InvalidOperationException(
                $"Startup schedule for '{typeof(TRequest)}' requires an explicit Portia system actor with a subject.");
        }
    }

    public ValueTask<string> EnsureAsync(IRequestScheduler scheduler, CancellationToken ct) =>
        scheduler.EnsureAsync(request, spec, routeValues, actor, ct);
}
