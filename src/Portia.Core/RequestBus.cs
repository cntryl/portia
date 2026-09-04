using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Dispatches through generated descriptors, resolving only the selected dependencies in the current scope.</summary>
/// <param name="services">The current application scope.</param>
/// <param name="registry">The composed generated registrations.</param>
public sealed class RequestBus(IServiceProvider services, RequestRegistry registry) : IRequestBus
{
    /// <inheritdoc />
    public async ValueTask<Result> SendAsync(IRequest request, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        Validate(request, actor, ct);
        using var activity = StartActivity(request);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, actor, ct).ConfigureAwait(false);
        var result = authorization.IsSuccess
            ? await ((IRequestInvocation)registration).InvokeAsync(services, request, ct).ConfigureAwait(false)
            : authorization;
        PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> SendAsync<TOut>(IRequest<TOut> request, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        Validate(request, actor, ct);
        using var activity = StartActivity(request);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, actor, ct).ConfigureAwait(false);
        var result = authorization.IsSuccess
            ? await ((IRequestInvocation<TOut>)registration).InvokeAsync(services, request, ct).ConfigureAwait(false)
            : Result<TOut>.Failure(authorization.Error!);
        PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
        return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> StreamAsync<TOut>(IStreamRequest<TOut> request, ClaimsPrincipal actor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Validate(request, actor, ct);
        using var activity = StartActivity(request);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, actor, ct).ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            PortiaTelemetry.RecordOutcome(activity, false, authorization.Error);
            throw new RequestAuthorizationException(authorization.Error!);
        }
        await foreach (var item in ((IStreamRequestInvocation<TOut>)registration).Invoke(services, request, ct).WithCancellation(ct).ConfigureAwait(false))
            yield return item;
    }

    async ValueTask<Result> AuthorizeAsync(RequestHandlerRegistration registration, IRequestBase request, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (registration.Permission is not null)
        {
            var permission = await services.GetRequiredService<IPermissionEvaluator>()
                .EvaluateAsync(actor, registration.Permission(request), ct).ConfigureAwait(false);
            if (!permission.IsSuccess)
                return permission;
        }
        var authorizer = registry.Authorizer(registration.RequestType);
        return authorizer is null ? Result.Success
            : await authorizer.AuthorizeAsync(services, request, actor, ct).ConfigureAwait(false);
    }

    static void Validate(IRequestBase request, ClaimsPrincipal actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
    }

    static Activity? StartActivity(IRequestBase request)
    {
        var name = request.GetType().Name;
        var activity = PortiaTelemetry.ActivitySource.StartActivity("Portia " + name);
        _ = activity?.SetTag("portia.request_type", name);
        return activity;
    }
}
