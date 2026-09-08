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
        using var activity = PortiaTelemetry.StartExecute(request.GetType().Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = Transport(context.Invocation);
        PortiaTelemetry.RequestStarted(request.GetType().Name, transport);
        Result result = default;
        var completed = false;
        try
        {
            var registration = registry.Handler(request.GetType());
            var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
            result = authorization.IsSuccess
                ? await ((IRequestInvocation)registration).InvokeAsync(services, request, context, ct).ConfigureAwait(false)
                : authorization;
            completed = true;
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally { PortiaTelemetry.RequestFinished(started, request.GetType().Name, transport, completed ? PortiaTelemetry.Outcome(result.IsSuccess, result.Error) : (ct.IsCancellationRequested ? "canceled" : "fault")); }
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context.Actor, ct);
        using var activity = PortiaTelemetry.StartExecute(request.GetType().Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = Transport(context.Invocation);
        PortiaTelemetry.RequestStarted(request.GetType().Name, transport);
        Result<TOut> result = default;
        var completed = false;
        try
        {
            var registration = registry.Handler(request.GetType());
            var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
            result = authorization.IsSuccess
                ? await ((IRequestInvocation<TOut>)registration).InvokeAsync(services, request, context, ct).ConfigureAwait(false)
                : Result<TOut>.Failure(authorization.Error!);
            completed = true;
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            return result;
        }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally { PortiaTelemetry.RequestFinished(started, request.GetType().Name, transport, completed ? PortiaTelemetry.Outcome(result.IsSuccess, result.Error) : (ct.IsCancellationRequested ? "canceled" : "fault")); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context.Actor, ct);
        using var activity = PortiaTelemetry.StartExecute(request.GetType().Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = Transport(context.Invocation);
        PortiaTelemetry.RequestStarted(request.GetType().Name, transport);
        var outcome = "fault";
        try
        {
            var registration = registry.Handler(request.GetType());
            var authorization = await AuthorizeAsync(registration, request, context, ct).ConfigureAwait(false);
            if (!authorization.IsSuccess)
            {
                PortiaTelemetry.RecordOutcome(activity, false, authorization.Error);
                outcome = PortiaTelemetry.Outcome(false, authorization.Error);
                throw new RequestAuthorizationException(authorization.Error!);
            }
            await foreach (var item in ((IStreamRequestInvocation<TOut>)registration).Invoke(services, request, context, ct).WithCancellation(ct).ConfigureAwait(false))
                yield return item;
            outcome = "success";
        }
        finally { PortiaTelemetry.RequestFinished(started, request.GetType().Name, transport, ct.IsCancellationRequested ? "canceled" : outcome); }
    }

    async ValueTask<Result> AuthorizeAsync(RequestHandlerRegistration registration, IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        var authorizers = registry.Authorizers(registration.RequestType).ToArray();
        foreach (var authorizer in authorizers.Where(candidate => candidate.Stage < AuthorizationStage.ResourceAccess))
        {
            var started = PortiaTelemetry.StartTimestamp();
            var result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
            PortiaTelemetry.AuthorizationFinished(started, authorizer.GetType().Name, authorizer.Stage.ToString().ToLowerInvariant(), PortiaTelemetry.Outcome(result.IsSuccess, result.Error));
            if (!result.IsSuccess)
                return result;
        }
        if (registration.Permission is not null)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var permission = await services.GetRequiredService<IPermissionEvaluator>()
                .EvaluateAsync(context.Actor, registration.Permission(request), ct).ConfigureAwait(false);
            PortiaTelemetry.AuthorizationFinished(started, "permission", "permission", PortiaTelemetry.Outcome(permission.IsSuccess, permission.Error));
            if (!permission.IsSuccess)
                return permission;
        }
        foreach (var authorizer in authorizers.Where(candidate => candidate.Stage >= AuthorizationStage.ResourceAccess))
        {
            var started = PortiaTelemetry.StartTimestamp();
            var result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
            PortiaTelemetry.AuthorizationFinished(started, authorizer.GetType().Name, authorizer.Stage.ToString().ToLowerInvariant(), PortiaTelemetry.Outcome(result.IsSuccess, result.Error));
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

    static string Transport(RequestInvocation invocation) => invocation switch
    {
        HttpInvocation => "http",
        RpcInvocation => "fitz.rpc",
        QueueInvocation => "fitz.queue",
        NoticeInvocation => "fitz.notice",
        ScheduleInvocation => "fitz.schedule",
        _ => "local",
    };
}
