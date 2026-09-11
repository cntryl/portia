using System.Security.Claims;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

interface IRequestScheduleDeclaration
{
    ValueTask<string> EnsureAsync(IRequestScheduler scheduler, CancellationToken ct);
}

sealed class RequestScheduleDeclaration<TRequest>(TRequest request, RequestScheduleSpec spec,
    RequestRouteValues routeValues, ClaimsPrincipal actor) : IRequestScheduleDeclaration
    where TRequest : IRequest, ISchedulable
{
    public ValueTask<string> EnsureAsync(IRequestScheduler scheduler, CancellationToken ct)
    {
        if (!RequestActor.IsSystem(actor) || actor.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) is null)
        {
            throw new InvalidOperationException(
                $"Startup schedule for '{typeof(TRequest)}' requires an explicit Portia system actor with a subject.");
        }

        return scheduler.EnsureAsync(request, spec, routeValues, actor, ct);
    }
}

sealed class RequestScheduleStartupService(IEnumerable<IRequestScheduleDeclaration> declarations,
    IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scheduler = services.GetService(typeof(IRequestScheduler)) as IRequestScheduler
                        ?? throw new InvalidOperationException(
                            "Portia startup schedules require an IRequestScheduler. Configure a scheduling provider, such as AddFitz(), before starting workers.");
        foreach (var declaration in declarations)
            _ = await declaration.EnsureAsync(scheduler, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
