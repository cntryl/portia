using System.Security.Claims;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

interface IRequestScheduleDeclaration
{
    void Validate();
    ValueTask<string> EnsureAsync(IRequestScheduler scheduler, CancellationToken ct);
}

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

sealed class RequestScheduleStartupService(
    IEnumerable<IRequestScheduleDeclaration> declarations,
    IServiceProvider services,
    PortiaStartupValidationRegistry validations) : IHostedLifecycleService
{
    bool _deferred;
    IRequestScheduler? _scheduler;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var declaration in declarations)
            declaration.Validate();
        _scheduler = services.GetService(typeof(IRequestScheduler)) as IRequestScheduler
                     ?? throw new InvalidOperationException(
                         "Portia startup schedules require an IRequestScheduler. Configure a scheduling provider, such as AddFitz(), before starting workers.");
        if (!validations.HasPendingEndpointValidations)
            return EnsureAsync(_scheduler, cancellationToken);
        _deferred = true;
        return Task.CompletedTask;
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!_deferred)
            return;
        _deferred = false;
        await validations.WaitForEndpointValidationsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAsync(_scheduler!, cancellationToken).ConfigureAwait(false);
    }

    async Task EnsureAsync(IRequestScheduler scheduler, CancellationToken cancellationToken)
    {
        foreach (var declaration in declarations)
            _ = await declaration.EnsureAsync(scheduler, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
