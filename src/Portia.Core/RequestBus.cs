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
    public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null)
        => new(actor, metadata: metadata, timeProvider: services.GetService<TimeProvider>());

    /// <inheritdoc />
    public async ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context.Actor, ct);
        using var activity = StartActivity(request, context);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
        var result = authorization.IsSuccess
            ? await ((IRequestInvocation)registration).InvokeAsync(services, request, context, ct).ConfigureAwait(false)
            : authorization;
        PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context.Actor, ct);
        using var activity = StartActivity(request, context);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
        var result = authorization.IsSuccess
            ? await ((IRequestInvocation<TOut>)registration).InvokeAsync(services, request, context, ct).ConfigureAwait(false)
            : Result<TOut>.Failure(authorization.Error!);
        PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
        return result;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context.Actor, ct);
        using var activity = StartActivity(request, context);
        var registration = registry.Handler(request.GetType());
        var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            PortiaTelemetry.RecordOutcome(activity, false, authorization.Error);
            throw new RequestAuthorizationException(authorization.Error!);
        }
        await foreach (var item in ((IStreamRequestInvocation<TOut>)registration).Invoke(services, request, context, ct).WithCancellation(ct).ConfigureAwait(false))
            yield return item;
    }

    async ValueTask<Result> AuthorizeAsync(RequestHandlerRegistration registration, IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        var authorizers = registry.Authorizers(registration.RequestType).ToArray();
        foreach (var authorizer in authorizers.Where(candidate => candidate.Stage < AuthorizationStage.ResourceAccess))
        {
            var result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return result;
        }
        if (registration.Permission is not null)
        {
            var permission = await services.GetRequiredService<IPermissionEvaluator>()
                .EvaluateAsync(context.Actor, registration.Permission(request), ct).ConfigureAwait(false);
            if (!permission.IsSuccess)
                return permission;
        }
        foreach (var authorizer in authorizers.Where(candidate => candidate.Stage >= AuthorizationStage.ResourceAccess))
        {
            var result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return result;
        }
        return Result.Success;
    }

    static void Validate(IRequestBase request, ClaimsPrincipal actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
    }

    static Activity? StartActivity(IRequestBase request, RequestDispatchContext context)
    {
        var name = request.GetType().Name;
        var activity = PortiaTelemetry.ActivitySource.StartActivity("Portia " + name);
        _ = activity?.SetTag("portia.request_type", name);
        _ = activity?.SetTag("portia.request_id", context.RequestId.ToString());
        _ = activity?.SetTag("portia.execution_id", context.ExecutionId.ToString());
        _ = activity?.SetTag("portia.correlation_id", context.CorrelationId.ToString());
        return activity;
    }
}
